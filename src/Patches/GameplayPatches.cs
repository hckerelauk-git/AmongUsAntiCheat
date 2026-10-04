using System;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using ApexCheatEnder.Core;
using HarmonyLib;
using UnityEngine;

namespace ApexCheatEnder.Patches
{
    /// <summary>
    /// RPC 上下文：记录「当前正在处理哪条 RPC、发送者是谁」。
    ///
    /// 存在的原因：有些方法（如 <c>ShipStatus.UpdateSystem</c>）的签名里**没有玩家信息**，
    /// 而它们又是由某条 RPC 触发的。做法是在 <c>PlayerControl.HandleRpc</c> 的 Prefix 里
    /// 记下发送者，下游补丁再从上下文里取——避免去解析 MessageReader。
    ///
    /// 用**栈**而不是单个静态字段：RPC 处理过程中可能嵌套触发另一条 RPC
    /// （A 的 HandleRpc → 游戏逻辑 → B 的 HandleRpc）。用单字段的话，
    /// 内层会覆盖外层，等外层继续往下走时读到的就是**别人的发送者**，
    /// 证据会归错人。栈结构天然支持嵌套，Prefix push / Postfix pop。
    /// </summary>
    internal static class RpcContext
    {
        private struct Frame
        {
            public PlayerControl Sender;
            public int CallId;
        }

        private static readonly Stack<Frame> Stack = new Stack<Frame>(8);

        /// <summary>当前正在处理的 RPC 的发送者；栈空时为 null。</summary>
        public static PlayerControl Sender =>
            Stack.Count > 0 ? Stack.Peek().Sender : null;

        /// <summary>当前正在处理的 RPC 的 callId；栈空时为 -1。</summary>
        public static int CallId =>
            Stack.Count > 0 ? Stack.Peek().CallId : -1;

        public static void Push(PlayerControl sender, int callId) =>
            Stack.Push(new Frame { Sender = sender, CallId = callId });

        public static void Pop()
        {
            if (Stack.Count > 0) Stack.Pop();
        }

        /// <summary>取发送者对应的玩家轨迹；拿不到返回 null。</summary>
        public static PlayerTrack SenderTrack()
        {
            var sender = Sender;
            if (sender == null) return null;
            var id = GameBridge.GetPlayerId(sender);
            if (id < 0) return null;
            return AntiCheatRuntime.GetOrCreateTrack(id, GameBridge.GetPlayerName(sender));
        }
    }

    /// <summary>
    /// 在 HandleRpc 入口记录发送者，出口弹出，供没有玩家参数的下游补丁取用。
    /// Prefix 与 Postfix 必须成对 —— 少了 Postfix 栈会无限增长。
    /// </summary>
    [HarmonyPatch]
    internal static class RpcContextPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "HandleRpc");

        private static void Prefix(PlayerControl __instance, object[] __args)
        {
            try
            {
                var callId = ExtractCallId(__args);

                // 模组指纹：超出原版 RpcCalls 范围（>67）的 callId 一定是模组发的。
                // 挂在这里是因为 HandleRpc 的 Prefix 能看到**每一个** RPC 及其发送者。
                // Observe 对原版范围内的 callId 立即返回，热路径开销可忽略。
                ModFingerprint.Observe(__instance, callId);

                RpcContext.Push(__instance, callId);
            }
            catch
            {
                // 入栈失败也要压一个空帧，保证 Prefix/Postfix 配对，
                // 否则 Pop 会把外层的帧弹掉，导致外层读到 null。
                try { RpcContext.Push(null, -1); } catch { }
            }
        }

        private static void Postfix()
        {
            try { RpcContext.Pop(); } catch { }
        }

        private static int ExtractCallId(object[] args)
        {
            if (args == null) return -1;
            foreach (var a in args)
            {
                if (a is byte b) return b;
                if (a is sbyte sb) return sb;
                if (a is int i) return i;
            }
            return -1;
        }
    }

    // ======================================================================
    //  破坏系统
    // ======================================================================

    /// <summary>
    /// 破坏系统：<c>ShipStatus.UpdateSystem(SystemTypes, byte)</c>。
    /// 签名里没有玩家信息，因此发送者从 <see cref="RpcContext"/> 取。
    /// </summary>
    [HarmonyPatch]
    internal static class UpdateSystemPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(ShipStatus), "UpdateSystem");

        private static void Postfix(object[] __args)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady) return;

                // ── 归因必须先确认「栈顶那条 RPC 就是 UpdateSystem」 ──
                //
                // RpcContext 记的是「当前正在处理的 RPC」，不是「当前这个方法是被谁调的」。
                // 而 ShipStatus.UpdateSystem 既可能由 UpdateSystem 这条 RPC 触发，
                // 也可能在处理**别的** RPC（比如击杀）途中被游戏内部调用。
                //
                // 不做这个判断的话，后者会被算成「这个杀人的人搞破坏」——
                // 现场日志里就是这样：一次击杀同时报了 KillWhileNotImpostor 和
                // SabotageWhileNotImpostor，用户看到的就是「明明是杀人却报了破坏」。
                //
                // 栈空（本地调用，非 RPC）时 CallId 为 -1，同样不归因 ——
                // 本地调用是本机自己的行为，本来也不该算到别人头上。
                if (RpcContext.CallId != (int)RpcCalls.UpdateSystem) return;

                var track = RpcContext.SenderTrack();
                if (track == null) return;

                var systemId = ExtractFirstNumeric(__args);
                var systemName = SystemNameOf(systemId);

                var sender = RpcContext.Sender;
                var buffer = new List<Violation>(2);

                AntiCheatRuntime.Analyzer.AnalyzeSabotage(
                    track,
                    GameBridge.IsImpostor(sender),
                    GameBridge.IsRoleKnown(sender),
                    GameBridge.IsDead(sender),
                    systemName,
                    GameBridge.IsInMeeting,
                    Time.time,
                    buffer);

                // 越界目标（伪造 / 改包）
                var validCount = SystemTypeCount;
                if (validCount > 0)
                {
                    AntiCheatRuntime.Analyzer.AnalyzeSabotageTarget(
                        track, systemName, systemId, validCount, Time.time, buffer);
                }

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);
            });
        }

        private static int _systemTypeCount = -1;
        private static int SystemTypeCount
        {
            get
            {
                if (_systemTypeCount >= 0) return _systemTypeCount;
                try { _systemTypeCount = Enum.GetValues(typeof(SystemTypes)).Length; }
                catch { _systemTypeCount = 0; }
                return _systemTypeCount;
            }
        }

        private static int ExtractFirstNumeric(object[] args)
        {
            if (args == null) return -1;
            foreach (var a in args)
            {
                if (a is byte b) return b;
                if (a is sbyte sb) return sb;
                if (a is int i) return i;
                if (a == null) continue;
                var t = a.GetType();
                if (t.IsEnum)
                {
                    try { return Convert.ToInt32(a); } catch { }
                }
            }
            return -1;
        }

        private static string SystemNameOf(int id)
        {
            try
            {
                if (id >= 0 && Enum.IsDefined(typeof(SystemTypes), (byte)id))
                    return ((SystemTypes)(byte)id).ToString();
            }
            catch { }
            return "未知系统(" + id + ")";
        }
    }

    // ======================================================================
    //  会议
    // ======================================================================

    /// <summary>开局保护期内的会议 / 报告尸体。</summary>
    [HarmonyPatch]
    internal static class ReportDeadBodyPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "ReportDeadBody");

        private static void Postfix(PlayerControl __instance)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady || __instance == null) return;

                var id = GameBridge.GetPlayerId(__instance);
                if (id < 0) return;

                var track = AntiCheatRuntime.GetOrCreateTrack(id, GameBridge.GetPlayerName(__instance));
                var elapsed = Time.time - AntiCheatRuntime.RoundStartTime;

                var buffer = new List<Violation>(2);
                AntiCheatRuntime.Analyzer.AnalyzeEarlyMeeting(
                    track, elapsed, "会议 / 报告尸体", Time.time, buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);
            });
        }
    }

    // ======================================================================
    //  聊天
    // ======================================================================

    /// <summary>聊天消息：独立本地疑似骂人提示；原有刷屏与内容合法性分析保持不变。</summary>
    [HarmonyPatch]
    internal static class SendChatPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "RpcSendChat");

        private static void Postfix(PlayerControl __instance, object[] __args)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady || __instance == null) return;

                var id = GameBridge.GetPlayerId(__instance);
                if (id < 0) return;

                var track = AntiCheatRuntime.GetOrCreateTrack(id, GameBridge.GetPlayerName(__instance));
                var text = PatchHelper.FirstArgOfType<string>(__args);

                var buffer = new List<Violation>(2);
                AntiCheatRuntime.Analyzer.AnalyzeChat(track, text, Time.time, buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);
                AntiCheatRuntime.Recorder?.Record(id, "SendChat");
            });
        }
    }

    // 在聊天呈现入口观察，覆盖本地发送和远端接收；不与 RpcSendChat 重复计数。
    [HarmonyPatch]
    internal static class ChatNoticePatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(ChatController), "AddChat");

        private static void Postfix(object[] __args)
        {
            PatchHelper.Safe(() => UI.ChatAbuseNotice.Observe(
                GameBridge.GetPlayerId(PatchHelper.FirstArgOfType<PlayerControl>(__args)),
                PatchHelper.FirstArgOfType<string>(__args)));
        }
    }

    // ======================================================================
    //  昵称
    // ======================================================================

    /// <summary>昵称合法性。</summary>
    [HarmonyPatch]
    internal static class SetNamePatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "RpcSetName");

        private static void Postfix(PlayerControl __instance, object[] __args)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady || __instance == null) return;

                var id = GameBridge.GetPlayerId(__instance);
                if (id < 0) return;

                var raw = PatchHelper.FirstArgOfType<string>(__args);
                var track = AntiCheatRuntime.GetOrCreateTrack(id, GameBridge.GetPlayerName(__instance));

                var buffer = new List<Violation>(2);
                AntiCheatRuntime.Analyzer.AnalyzeName(track, raw, Time.time, buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);
            });
        }
    }

    // ======================================================================
    //  角色动作
    // ======================================================================

    /// <summary>变形：只有变形者能变形。</summary>
    [HarmonyPatch]
    internal static class ShapeshiftPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "RpcShapeshift");

        private static void Postfix(PlayerControl __instance)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady || __instance == null) return;

                var id = GameBridge.GetPlayerId(__instance);
                if (id < 0) return;

                var track = AntiCheatRuntime.GetOrCreateTrack(id, GameBridge.GetPlayerName(__instance));

                var buffer = new List<Violation>(2);
                AntiCheatRuntime.Analyzer.AnalyzeShapeshift(
                    track,
                    GameBridge.IsShapeshifter(__instance),
                    GameBridge.IsRoleKnown(__instance),
                    GameBridge.IsDead(__instance),
                    Time.time,
                    buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);
            });
        }
    }

    /// <summary>保护：只有守护天使能保护。</summary>
    [HarmonyPatch]
    internal static class ProtectPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "RpcProtectPlayer");

        private static void Postfix(PlayerControl __instance)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady || __instance == null) return;

                var id = GameBridge.GetPlayerId(__instance);
                if (id < 0) return;

                var track = AntiCheatRuntime.GetOrCreateTrack(id, GameBridge.GetPlayerName(__instance));

                var buffer = new List<Violation>(2);
                AntiCheatRuntime.Analyzer.AnalyzeProtect(
                    track,
                    GameBridge.IsGuardianAngel(__instance),
                    GameBridge.IsRoleKnown(__instance),
                    GameBridge.IsDead(__instance),
                    Time.time,
                    buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);
            });
        }
    }

    // ======================================================================
    //  通风管 / 滑索
    // ======================================================================

    /// <summary>
    /// 进入通风管：伪造管道编号、会议期间使用。
    ///
    /// 直接挂 <c>PlayerControl.RpcEnterVent(int)</c> —— 它的参数里就带着 ventId，
    /// 比绕道 VentilationSystem 的内部字典更直接、也更稳。
    /// </summary>
    [HarmonyPatch]
    internal static class VentOpPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "RpcEnterVent");

        private static void Postfix(PlayerControl __instance, object[] __args)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady || __instance == null) return;

                var id = GameBridge.GetPlayerId(__instance);
                if (id < 0) return;

                var track = AntiCheatRuntime.GetOrCreateTrack(id, GameBridge.GetPlayerName(__instance));

                var ventId = -1;
                foreach (var a in __args ?? Array.Empty<object>())
                {
                    if (a is int i) { ventId = i; break; }
                    if (a is byte b) { ventId = b; break; }
                }

                var valid = ventId >= 0 ? GameBridge.IsValidVentId(ventId) : (bool?)null;

                var buffer = new List<Violation>(2);
                AntiCheatRuntime.Analyzer.AnalyzeVentOp(
                    track, ventId, valid, GameBridge.IsInMeeting, Time.time, buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);
            });
        }
    }

    /// <summary>
    /// 强制把他人踢出通风管（<c>ShipStatus.RpcBootFromVent</c>）。
    /// 这是房主专属逻辑；非房主触发即为权限提升类漏洞。
    /// </summary>
    [HarmonyPatch]
    internal static class BootFromVentPatch
    {
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("ShipStatus");
            return t == null ? null : PatchHelper.FindByName(t, "RpcBootFromVent");
        }

        private static void Prefix()
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady) return;
                if (GameBridge.IsHost) return;

                var local = GameBridge.GetLocalPlayer();
                if (local == null) return;

                var id = GameBridge.GetPlayerId(local);
                if (id < 0) return;

                var track = AntiCheatRuntime.GetOrCreateTrack(id, GameBridge.GetPlayerName(local));

                var buffer = new List<Violation>(2);
                AntiCheatRuntime.Analyzer.AnalyzeVentForce(
                    track, false, "RpcBootFromVent", Time.time, buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);
            });
        }
    }

    /// <summary>滑索：会议期间使用属于异常。</summary>
    [HarmonyPatch]
    internal static class ZiplinePatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "RpcUseZipline");

        private static void Postfix(PlayerControl __instance)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady || __instance == null) return;

                var id = GameBridge.GetPlayerId(__instance);
                if (id < 0) return;

                var track = AntiCheatRuntime.GetOrCreateTrack(id, GameBridge.GetPlayerName(__instance));

                var buffer = new List<Violation>(2);
                AntiCheatRuntime.Analyzer.AnalyzeZipline(
                    track, GameBridge.IsInMeeting, GameBridge.IsDead(__instance), Time.time, buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);
            });
        }
    }

    // ======================================================================
    //  网络：超大数据包
    // ======================================================================

    /// <summary>
    /// 超大数据包防护。走 Prefix，房主可直接丢弃。
    ///
    /// 用反射读 <c>MessageReader.Length</c>，避免在编译期依赖 Hazel 程序集
    /// （本项目并未直接引用它）。
    /// </summary>
    [HarmonyPatch]
    internal static class OversizedPacketPatch
    {
        private const int DefaultLimit = 1400;

        private static PropertyInfo _lengthProp;
        private static bool _lengthResolved;

        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("InnerNetClient");
            return t == null ? null : PatchHelper.FindByName(t, "HandleGameData");
        }

        private static bool Prefix(object[] __args)
        {
            try
            {
                if (!AntiCheatPlugin.EventScanEnabled) return true;
                if (!AntiCheatRuntime.IsReady) return true;
                if (!(AntiCheatRuntime.Config?.OversizedPacketCheck.Value ?? false)) return true;

                object reader = null;
                foreach (var a in __args ?? Array.Empty<object>())
                {
                    if (a != null && a.GetType().Name == "MessageReader") { reader = a; break; }
                }
                if (reader == null) return true;

                if (!_lengthResolved)
                {
                    _lengthResolved = true;
                    try { _lengthProp = reader.GetType().GetProperty("Length"); }
                    catch { _lengthProp = null; }
                }
                if (_lengthProp == null) return true;

                var raw = _lengthProp.GetValue(reader);
                if (raw == null) return true;
                var size = Convert.ToInt32(raw);

                if (size <= DefaultLimit) return true;

                var buffer = new List<Violation>(1);
                AntiCheatRuntime.Analyzer.AnalyzeOversizedPacket(
                    -1, "?", size, DefaultLimit, Time.time, buffer);
                foreach (var v in buffer) AntiCheatRuntime.Submit(v);

                // 只有房主真正丢弃；成员只记录
                return !GameBridge.IsHost;
            }
            catch
            {
                return true;
            }
        }
    }
}
