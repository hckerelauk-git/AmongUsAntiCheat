using System;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using AmongUsAntiCheat.Core;
using HarmonyLib;
using UnityEngine;

namespace AmongUsAntiCheat.Patches
{
    /// <summary>
    /// 补丁工具：按方法名在类型里查找目标方法。
    ///
    /// 不硬编码方法签名的原因：Among Us 每个大版本都会给这些核心方法加参数
    /// （例如 MurderPlayer 后来加了 MurderResultFlags）。按名字找 + 用 __args
    /// 通用取参，可以让补丁在签名变化后依然工作。
    /// </summary>
    internal static class PatchHelper
    {
        /// <summary>
        /// 在指定类型里找同名方法。
        /// 优先返回公开重载——公开方法才是游戏的正常调用入口，
        /// 私有重载往往是内部实现细节，patch 它容易漏事件。
        /// </summary>
        public static MethodBase FindByName(Type type, string methodName)
        {
            try
            {
                var methods = AccessTools.GetDeclaredMethods(type);
                var best = PickBest(methods, methodName, requirePublic: true);
                return best ?? PickBest(methods, methodName, requirePublic: false);
            }
            catch { return null; }
        }

        private static MethodBase PickBest(IEnumerable<MethodBase> methods, string name, bool requirePublic)
        {
            MethodBase best = null;
            foreach (var m in methods)
            {
                if (!string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
                if (requirePublic && !m.IsPublic) continue;
                if (best == null || m.GetParameters().Length > best.GetParameters().Length)
                    best = m;
            }
            return best;
        }

        /// <summary>从通用参数数组里挑出第一个指定类型的实参。</summary>
        public static T FirstArgOfType<T>(object[] args) where T : class
        {
            if (args == null) return null;
            foreach (var a in args)
            {
                if (a is T typed) return typed;
            }
            return null;
        }

        /// <summary>补丁内的统一安全入口：任何异常都不得影响游戏运行。</summary>
        public static void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                AntiCheatPlugin.LogSource?.LogWarning($"[补丁] 检测逻辑异常（已忽略）：{ex.Message}");
            }
        }
    }

    // ======================================================================
    //  击杀
    // ======================================================================

    /// <summary>拦截击杀动作，校验距离、冷却与角色合法性。</summary>
    [HarmonyPatch]
    internal static class MurderPlayerPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "MurderPlayer");

        private static void Postfix(PlayerControl __instance, object[] __args)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady || __instance == null) return;

                var killerId = GameBridge.GetPlayerId(__instance);
                if (killerId < 0) return;

                var killer = AntiCheatRuntime.GetOrCreateTrack(killerId, GameBridge.GetPlayerName(__instance));

                // 从参数里找受害者
                var victimControl = PatchHelper.FirstArgOfType<PlayerControl>(__args);
                PlayerTrack victim = null;
                if (victimControl != null)
                {
                    var victimId = GameBridge.GetPlayerId(victimControl);
                    if (victimId >= 0)
                        victim = AntiCheatRuntime.GetOrCreateTrack(victimId, GameBridge.GetPlayerName(victimControl));
                }

                var allowedDistance = GameBridge.GetAllowedKillDistance();
                var cooldown = GameBridge.GetKillCooldown();

                var buffer = new List<Violation>(4);
                AntiCheatRuntime.Analyzer.AnalyzeKill(killer, victim, Time.time, allowedDistance, cooldown, buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);

                // 把这次击杀事件喂给 AI 分析器做二次研判
                AntiCheatRuntime.Recorder?.Record(killerId, "MurderPlayer",
                    cooldown: cooldown,
                    distance: victim != null
                        ? GameVec2.Distance(killer.Current.Position, victim.Current.Position)
                        : 0f);
            });
        }
    }

    // ======================================================================
    //  任务
    // ======================================================================

    /// <summary>拦截任务完成，校验任务点位置与移动速度。</summary>
    [HarmonyPatch]
    internal static class CompleteTaskPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "CompleteTask");

        private static void Postfix(PlayerControl __instance, object[] __args)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady || __instance == null) return;

                var playerId = GameBridge.GetPlayerId(__instance);
                if (playerId < 0) return;

                var track = AntiCheatRuntime.GetOrCreateTrack(playerId, GameBridge.GetPlayerName(__instance));

                // 从参数里取任务 Id（uint），据此定位任务点
                var taskPosition = track.Current.Position;
                if (__args != null && __args.Length > 0)
                {
                    var taskId = TryExtractTaskId(__args[0]);
                    if (taskId >= 0)
                    {
                        var resolved = GameBridge.GetTaskPosition(__instance, taskId);
                        if (resolved.HasValue) taskPosition = resolved.Value;
                    }
                }

                var maxSpeed = GameBridge.GetMaxAllowedSpeed(__instance)
                             * AntiCheatRuntime.Config.MaxSpeedTolerance.Value;

                var buffer = new List<Violation>(4);
                AntiCheatRuntime.Analyzer.AnalyzeTask(track, taskPosition, Time.time, maxSpeed, buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);

                AntiCheatRuntime.Recorder?.Record(playerId, "CompleteTask");
            });
        }

        private static int TryExtractTaskId(object arg)
        {
            try
            {
                if (arg == null) return -1;
                if (arg is uint u) return (int)u;
                if (arg is int i) return i;
                if (arg is byte b) return b;
                // IL2CPP 下小整数可能以装箱形式出现，统一走 Convert
                return Convert.ToInt32(arg);
            }
            catch { return -1; }
        }
    }

    // ======================================================================
    //  通风管
    // ======================================================================

    /// <summary>拦截进入通风管。签名：Vent.EnterVent(PlayerControl pc)</summary>
    [HarmonyPatch]
    internal static class VentEnterPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(Vent), "EnterVent");

        private static void Postfix(Vent __instance, object[] __args)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady) return;

                var player = PatchHelper.FirstArgOfType<PlayerControl>(__args);
                if (player == null) return;

                var playerId = GameBridge.GetPlayerId(player);
                if (playerId < 0) return;

                var track = AntiCheatRuntime.GetOrCreateTrack(playerId, GameBridge.GetPlayerName(player));

                // 通风管本体就是 __instance
                var ventPos = track.Current.Position;
                if (__instance != null)
                {
                    try
                    {
                        var p = __instance.transform.position;
                        ventPos = new GameVec2(p.x, p.y);
                    }
                    catch { }
                }

                var buffer = new List<Violation>(4);
                AntiCheatRuntime.Analyzer.AnalyzeVentUse(track, ventPos, Time.time, buffer);

                foreach (var v in buffer) AntiCheatRuntime.Submit(v);

                AntiCheatRuntime.Recorder?.Record(playerId, "EnterVent");
            });
        }
    }

    // ======================================================================
    //  RPC 洪水 / 加载期防护
    // ======================================================================

    [HarmonyPatch]
    internal static class RpcFloodPatches
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("PlayerControl");
            return type == null ? null : PatchHelper.FindByName(type, "HandleRpc");
        }

        private static bool Prefix(PlayerControl __instance, object[] __args)
        {
            if (!AntiCheatRuntime.IsReady || __instance == null) return true;
            try
            {
                var id = GameBridge.GetPlayerId(__instance);
                if (id < 0) return true;
                byte callId = 0;
                foreach (var arg in __args ?? Array.Empty<object>())
                {
                    if (arg is byte b) { callId = b; break; }
                    if (arg is sbyte sb) { callId = (byte)sb; break; }
                    if (arg is int i) { callId = (byte)i; break; }
                }

                var inLoadingOrLobby = !GameBridge.IsInGame ||
                    AmongUsClient.Instance == null || !AmongUsClient.Instance.IsGameStarted;
                var blocked = AntiCheatRuntime.RpcFlood?.TryRecord(
                    id, GameBridge.GetPlayerName(__instance), ((RpcCalls)callId).ToString(),
                    Time.realtimeSinceStartup, inLoadingOrLobby) ?? false;

                if (blocked && GameBridge.IsHost)
                    return false;
            }
            catch { }
            return true;
        }
    }

    // ======================================================================
    //  位置同步
    // ======================================================================

    /// <summary>
    /// 记录位置强制同步（RpcSnapTo）的频率。
    ///
    /// 刻意不把 SnapTo 当作瞬移豁免——作弊者的瞬移恰恰就是靠它实现的。
    /// 正确做法是统计频率：正常对局中它极少发生（只在网络严重卡顿时），
    /// 而作弊者会持续触发。短时间内高频 SnapTo 本身就是强证据。
    /// </summary>
    [HarmonyPatch]
    internal static class SnapToPatch
    {
        private static readonly Dictionary<int, float> LastSnapTimes = new Dictionary<int, float>();
        private static readonly Dictionary<int, int> SnapCounts = new Dictionary<int, int>();

        private const float WindowSeconds = 10f;

        /// <summary>默认阈值；实际以配置里的「同步频率上限」为准。</summary>
        private const int DefaultSuspiciousCount = 6;

        private static int SuspiciousCount
        {
            get
            {
                try
                {
                    var cfg = AntiCheatRuntime.Config;
                    if (cfg?.SnapRateDetection != null && !cfg.SnapRateDetection.Value) return int.MaxValue;
                    return cfg?.SnapRateLimit?.Value ?? DefaultSuspiciousCount;
                }
                catch { return DefaultSuspiciousCount; }
            }
        }

        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("CustomNetworkTransform");
            // patch RpcSnapTo 而不是 SnapTo：它是网络入口，本地主动传送与远端同步都会经过，
            // 而 SnapTo 有 public/private 多个重载，容易漏掉其中一个。
            return t == null ? null : PatchHelper.FindByName(t, "RpcSnapTo");
        }

        private static void Postfix(object __instance, object[] __args)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                if (!AntiCheatRuntime.IsReady || __instance == null) return;

                var player = ResolveOwner(__instance);
                if (player == null) return;

                var playerId = GameBridge.GetPlayerId(player);
                if (playerId < 0) return;

                var now = Time.time;

                if (!LastSnapTimes.TryGetValue(playerId, out var last) || now - last > WindowSeconds)
                    SnapCounts[playerId] = 1;
                else
                    SnapCounts[playerId] = SnapCounts.TryGetValue(playerId, out var c) ? c + 1 : 1;

                LastSnapTimes[playerId] = now;

                var count = SnapCounts[playerId];
                if (count < SuspiciousCount) return;

                // 达到阈值后重置，避免同一次突发被反复上报
                SnapCounts[playerId] = 0;

                AntiCheatRuntime.Submit(new Violation(
                    ViolationKind.Teleport,
                    Severity.High,
                    playerId,
                    GameBridge.GetPlayerName(player),
                    now,
                    $"{WindowSeconds:F0} 秒内触发了 {count} 次位置强制同步（RpcSnapTo），远超正常网络抖动水平。",
                    new Dictionary<string, float>
                    {
                        ["snap_count"] = count,
                        ["window"] = WindowSeconds,
                    }));

                AntiCheatRuntime.Recorder?.Record(playerId, "SnapTo",
                    argsJson: $"{{\"count_in_{WindowSeconds:F0}s\":{count}}}");
            });
        }

        /// <summary>从 CustomNetworkTransform 实例反查所属玩家。</summary>
        private static PlayerControl ResolveOwner(object netTransform)
        {
            try
            {
                var component = netTransform as Component;
                if (component == null) return null;

                var owner = component.GetComponentInParent<PlayerControl>();
                if (owner != null) return owner;

                var parent = component.transform?.parent;
                if (parent != null) return parent.GetComponent<PlayerControl>();
            }
            catch { }
            return null;
        }

        /// <summary>回合切换时清理统计，避免跨局累积。</summary>
        public static void ResetStats()
        {
            LastSnapTimes.Clear();
            SnapCounts.Clear();
        }
    }
}
