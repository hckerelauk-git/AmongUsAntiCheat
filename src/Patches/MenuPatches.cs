using System.Reflection;
using AmongUsAntiCheat.UI;
using HarmonyLib;
using UnityEngine;

namespace AmongUsAntiCheat.Patches
{
    /// <summary>
    /// 主菜单背景替换。
    ///
    /// 挂两个点，缺一不可：
    ///   Start  —— 首次进入主菜单时应用
    ///   Update —— 低频巡检补刀。游戏在切场景、开关菜单时会重建背景对象，
    ///             只挂 Start 的话「从对局返回主菜单」时背景就还原了。
    ///
    /// 用 __instance 的 transform 作为搜索根，而不是 GameObject.Find：
    /// Find 只能找到激活对象，而且容易命中同名的无关对象（游戏里叫
    /// Background 的东西不止一个）。从主菜单自己的层级往下找才可靠。
    /// </summary>
    [HarmonyPatch]
    internal static class MainMenuArtStartPatch
    {
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("MainMenuManager");
            return t == null ? null : PatchHelper.FindByName(t, "Start");
        }

        private static void Postfix(object __instance)
        {
            if (!AntiCheatPlugin.EventScanEnabled) return;

            PatchHelper.Safe(() =>
            {
                var comp = __instance as Component;
                if (comp == null) return;
                MainMenuArt.Apply(comp.transform.root);
            });
        }
    }

    /// <summary>巡检补刀：背景被游戏重建后重新贴上。</summary>
    [HarmonyPatch]
    internal static class MainMenuArtUpdatePatch
    {
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("MainMenuManager");
            return t == null ? null : PatchHelper.FindByName(t, "Update");
        }

        private static void Postfix(object __instance)
        {
            PatchHelper.Safe(() =>
            {
                var comp = __instance as Component;
                if (comp == null) return;
                // 内部有 0.5s 节流，每帧调用不会造成负担
                MainMenuArt.Apply(comp.transform.root);
            });
        }
    }
}
