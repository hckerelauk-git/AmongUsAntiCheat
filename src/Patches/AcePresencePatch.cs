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

        private static bool Prefix(PlayerControl __instance, object[] __args)
        {
            try
            {
                byte callId = 0;
                MessageReader reader = null;

                foreach (var arg in __args ?? Array.Empty<object>())
                {
                    if (arg is byte b) { callId = b; continue; }
                    if (arg is MessageReader r) reader = r;
                }

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
