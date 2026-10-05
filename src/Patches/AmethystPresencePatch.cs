using System;
using System.Reflection;
using ApexCheatEnder.Core;
using HarmonyLib;
using Hazel;

namespace ApexCheatEnder.Patches
{
    /// <summary>
    /// 旁听 Amethyst 的模组客户端探测包，识别房间里的 Amethyst 用户。
    ///
    /// ────────────── 优先级为什么必须是 High ──────────────
    ///
    /// Amethyst 自己也挂在这个方法上，并且识别到自己的包后会
    /// <c>return false</c> 把包吃掉。Harmony 的规则是：某个 Prefix 返回 false 后，
    /// **排在它后面的 Prefix 会被跳过**。
    ///
    /// 所以如果我们用默认优先级排在 Amethyst 后面，在「双方都装」的场景下
    /// 就永远看不到这些包 —— 而那恰恰是我们最需要识别的场景。
    /// 用 High 抢在它前面，先旁听、再放行，它照常吃自己的包。
    ///
    /// ────────────── 为什么永远返回 true ──────────────
    ///
    /// 这一层只观察、不拦截。返回 false 会：
    ///   · 破坏 Amethyst 自己的互认（它的 Prefix 被跳过，收不到探测也就不会应答）
    ///   · 在只有 ACE 的环境里改变游戏对 callId 50 的原有处理
    /// 两者都是我们不该做的事。
    /// </summary>
    [HarmonyPatch]
    internal static class AmethystPresencePatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(PlayerPhysics), "HandleRpc");

        /// <summary>
        /// **强类型参数，不能用 <c>object[] __args</c>。**
        /// PlayerPhysics.HandleRpc 是移动同步的必经之路，频率比 PlayerControl 那条还高。
        /// 用 <c>__args</c> 等于每个移动包都白送一次数组分配 + 装箱。
        /// </summary>
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(PlayerPhysics __instance,
            [HarmonyArgument(0)] byte callId,
            [HarmonyArgument(1)] MessageReader reader)
        {
            try
            {
                if (callId == AmethystPresence.AmethystCallId)
                {
                    var sender = __instance?.myPlayer;
                    if (reader != null)
                        AmethystPresence.TryAccept(callId, reader, sender);
                }
            }
            catch
            {
                // 识别失败不影响任何事：我们只是旁听者。
            }

            return true;
        }
    }
}
