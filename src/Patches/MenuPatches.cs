using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ApexCheatEnder.UI;

namespace ApexCheatEnder.Patches
{
    // Start 记录当前菜单，Update 每帧驱动 Tick。
    [HarmonyPatch]
    internal static class MainMenuArtStartPatch
    {
        private static MethodBase TargetMethod() => PatchHelper.FindByName(typeof(MainMenuManager), "Start");

        private static void Postfix(object __instance)
        {
            PatchHelper.Safe(() =>
            {
                var menu = __instance as MainMenuManager;
                if (menu != null) MainMenuArt.Apply(menu);
            });
        }
    }

    /// <summary>
    /// 每帧驱动主菜单背景的持续检查（重申渲染器关闭状态、透明色维持）。
    /// </summary>
    [HarmonyPatch]
    internal static class MainMenuArtUpdatePatch
    {
        private static MethodBase TargetMethod() => PatchHelper.FindByName(typeof(MainMenuManager), "Update");

        private static void Postfix()
        {
            PatchHelper.Safe(() => MainMenuArt.Tick());
        }
    }
}
