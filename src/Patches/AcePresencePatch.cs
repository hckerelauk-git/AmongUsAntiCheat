using System;
using System.Reflection;
using ApexCheatEnder.Core;
using HarmonyLib;
using Hazel;

namespace ApexCheatEnder.Patches
{
    /// <summary>
    /// 拦截 ACE 握手 RPC。
    ///
    /// 命中时返回 false 阻止原方法继续执行 —— 游戏的 HandleRpc 里没有
    /// 0xF0 这个分支，交给它处理只会走 default 打一条警告日志。拦下来更干净。
    ///
    /// Prefix/Postfix 配对说明：本补丁只有 Prefix。返回 false 时 Harmony 会
    /// 跳过原方法与尚未执行的 Prefix，但**已执行过**的 Prefix 对应的 Postfix
    /// 仍会执行 —— 所以 RpcContextPatch 的 push/pop 不会被我们拆散。
    /// </summary>
    [HarmonyPatch]
    internal static class AcePresenceRpcPatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerControl), "HandleRpc");

        /// <summary>
        /// **参数必须强类型声明，不能用 <c>object[] __args</c>。**
        ///
        /// HandleRpc 是每个 RPC 都要跑的方法，对局中每秒几十到上百次。
        /// 用 <c>object[] __args</c> 时 Harmony 每次调用都要
        /// **新建一个 object 数组 + 把每个参数装箱** —— 纯粹的垃圾，
        /// 累积后触发 GC，表现为周期性掉帧。
        ///
        /// 强类型参数直接读 IL 实参，零分配、零装箱，
        /// 热路径上只剩一次 byte 比较。
        /// </summary>
        private static bool Prefix(PlayerControl __instance,
            [HarmonyArgument(0)] byte callId,
            [HarmonyArgument(1)] MessageReader reader)
        {
            try
            {
                // 不是我们的 callId，直接放行，不做任何多余判断
                if (callId != AcePresence.MagicCallId) return true;

                if (AcePresence.TryAccept(callId, reader, __instance))
                    return false;   // 是我们的握手包，游戏不必再看
            }
            catch
            {
                // 解析失败一律放行，交给游戏自己处理
            }

            return true;
        }
    }
}
