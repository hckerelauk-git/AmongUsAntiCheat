using System;
using System.Collections.Generic;
using System.Reflection;
using ApexCheatEnder.Core;
using HarmonyLib;
using UnityEngine;

namespace ApexCheatEnder.Patches
{
    /// <summary>
    /// 接收端拦截：在 <c>PlayerControl.HandleRpc</c> 的 Prefix 里，
    /// 对**物理上不可能**的 RPC 直接返回 false，让它在本机根本不生效。
    ///
    /// ────────────── 为什么必须有这一层 ──────────────
    ///
    /// 好友房主的现场日志暴露了核心问题：ACE 的击杀 / 任务 / 管道 / 位置同步
    /// 补丁全是 <c>Postfix</c> —— 动作**执行完之后**才收集证据。
    /// 于是「检测到了」和「拦住了」是两件事，玩家看到的是作弊照样生效。
    ///
    /// 真正能丢包的只有 Prefix。本类只处理两类**语义确定、不会误伤**的 RPC：
    ///
    ///   MurderPlayer(12) 发送者角色明确不允许使用击杀按钮
    ///   EnterVent(19)    发送者角色明确不允许钻管道
    ///
    /// ────────────── 为什么只拦这两个 ──────────────
    ///
    /// 拦截的代价是不可逆的：判错一次，玩家就会看到「内鬼砍不动人」这种致命异常。
    /// 所以这里遵循一条硬规则 ——
    ///
    ///   **拿不到角色信息，一律放行。**
    ///
    /// 角色同步有延迟（刚进局、刚变形、刚复活时 <c>Data.Role</c> 可能还没到位），
    /// 此时 <see cref="GameBridge.TryGetCanKill"/> 返回 false，我们就放行，
    /// 宁可漏拦也不能砍掉内鬼的正常击杀。
    ///
    /// 位置类（SnapTo）、会议类（StartMeeting）暂时不拦：
    /// 前者是传送/动画的合法通道，后者会打断正常报告，都缺少可靠的判定依据。
    ///
    /// ────────────── 拦截范围说明 ──────────────
    ///
    /// 只在**房主**生效。Among Us 的 RPC 由服务端转发给所有客户端，
    /// 本机拦下只影响本机所见；房主是最有意义的一侧（也与你实测的场景一致）。
    /// 非房主一侧不动，避免各客户端表现不一致。
    /// </summary>
    [HarmonyPatch]
    internal static class RpcGuardPatch
    {
        /// <summary>同一条拦截日志的最小间隔（秒），避免刷屏。</summary>
        private const float LogCooldown = 5f;

        private static readonly Dictionary<int, float> LastLogged = new Dictionary<int, float>();

        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "HandleRpc");

        /// <summary>
        /// 排在 RpcContextPatch 之后执行。
        ///
        /// 这里有个必须守住的约束：Harmony 在某个 Prefix 返回 false 时，
        /// 会跳过尚未执行的 Prefix，但**所有 Postfix 仍会执行**。
        /// 用低优先级保证本 Prefix 一定在 RpcContext.Push 之后跑，
        /// 这样我们返回 false 时它的 Pop 正好配对，不会把外层栈帧弹掉。
        /// </summary>
        [HarmonyPriority(Priority.Low)]
        private static bool Prefix(PlayerControl __instance, object[] __args)
        {
            if (!AntiCheatRuntime.IsReady || __instance == null) return true;

            try
            {
                // 非房主不拦：本机拦了也改不了别人的画面，只会造成表现不一致。
                if (!GameBridge.IsHost) return true;
                if (!GameBridge.IsInGame) return true;

                var callId = ExtractCallId(__args);
                if (callId < 0) return true;

                switch ((RpcCalls)callId)
                {
                    case RpcCalls.MurderPlayer:
                        return !BlockImpossibleKill(__instance);

                    case RpcCalls.EnterVent:
                        return !BlockImpossibleVent(__instance);

                    default:
                        return true;
                }
            }
            catch
            {
                // 拦截层任何异常都必须放行 —— 拦错比漏拦严重得多。
            }

            return true;
        }

        /// <summary>角色明确不允许击杀 → 拦下这次 MurderPlayer。</summary>
        private static bool BlockImpossibleKill(PlayerControl actor)
        {
            if (!GameBridge.TryGetCanKill(actor, out var canKill)) return false;
            if (canKill) return false;

            var id = GameBridge.GetPlayerId(actor);
            var name = GameBridge.GetPlayerName(actor);

            Report(actor, id, name, ViolationKind.KillWhileNotImpostor, Severity.Critical,
                "接收端拦截：角色不允许使用击杀按钮，已丢弃 MurderPlayer RPC。");

            return true;
        }

        /// <summary>角色明确不允许钻管道 → 拦下这次 EnterVent。</summary>
        private static bool BlockImpossibleVent(PlayerControl actor)
        {
            if (!GameBridge.TryGetCanVent(actor, out var canVent)) return false;
            if (canVent) return false;

            var id = GameBridge.GetPlayerId(actor);
            var name = GameBridge.GetPlayerName(actor);

            Report(actor, id, name, ViolationKind.IllegalVent, Severity.Critical,
                "接收端拦截：角色不允许钻管道，已丢弃 EnterVent RPC。");

            return true;
        }

        /// <summary>提交证据并限频落日志。</summary>
        private static void Report(
            PlayerControl actor, int id, string name, ViolationKind kind, Severity severity, string detail)
        {
            var now = Time.realtimeSinceStartup;

            if (id >= 0)
            {
                if (LastLogged.TryGetValue(id, out var last) && now - last < LogCooldown) return;
                LastLogged[id] = now;
            }

            AntiCheatRuntime.Log?.LogWarning($"[拦截] 玩家「{name}」({id}) {detail}");

            try
            {
                AntiCheatRuntime.Submit(new Violation(kind, severity, id, name, Time.time, detail));
            }
            catch { }
        }

        /// <summary>新回合清空限频表，避免字典随玩家数无限增长。</summary>
        public static void ResetLogState() => LastLogged.Clear();

        private static int ExtractCallId(object[] args)
        {
            if (args == null) return -1;
            foreach (var arg in args)
            {
                if (arg is byte b) return b;
                if (arg is sbyte sb) return sb;
            }
            return -1;
        }
    }
}
