using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ApexCheatEnder.UI;

namespace ApexCheatEnder.Patches
{
    // Start 记录当前菜单，持续刷新与离场清理由已有 Canvas 主线程驱动。
    [HarmonyPatch]
    internal static class MainMenuArtStartPatch
    {
        private static MethodBase TargetMethod() => PatchHelper.FindByName(typeof(MainMenuManager), "Start");

        private static void Postfix(object __instance)
        {
            PatchHelper.Safe(() =>
            {
                var component = __instance as Component;
                if (component != null) MainMenuArt.Apply(component.transform);
            });
        }
    }
}
