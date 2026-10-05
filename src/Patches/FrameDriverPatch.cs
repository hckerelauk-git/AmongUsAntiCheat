using System.Reflection;
using ApexCheatEnder.UI;
using HarmonyLib;

namespace ApexCheatEnder.Patches
{
    /// <summary>
    /// 帧驱动：整个反作弊的心跳。
    ///
    /// 为什么不用自定义 MonoBehaviour 的 Update：
    /// 动态注册进 il2cpp 的托管组件收不到 Unity 每帧分发的消息
    /// （Awake 会在 AddComponent 时同步触发，Update 不会），
    /// 实测表现为插件加载成功、日志正常，但面板始终不出现。
    ///
    /// 现在改为 patch 原生方法，并同时挂三个入口互为冗余：
    ///   1. Canvas.SendWillRenderCanvases  每帧静态方法，启动到退出一直存在（主入口）
    ///   2. HudManager.Update              大厅 / 对局中
    ///   3. SplashManager.Update           启动阶段
    ///
    /// 三个入口都调 <see cref="FrameDriver.Drive"/>，
    /// 而 AceUiRoot.DriveFrame 内部做了帧去重，同帧重复触发不会重复执行。
    /// </summary>
    internal static class FrameDriver
    {
        internal static void Drive() => AceUiRoot.DriveFrame();
    }

    /// <summary>主入口：Unity 每帧渲染 Canvas 前调用，游戏任何阶段都存在。</summary>
    [HarmonyPatch]
    internal static class CanvasFramePatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("UnityEngine.Canvas");
            return type == null ? null : PatchHelper.FindByName(type, "SendWillRenderCanvases");
        }

        private static void Postfix() => FrameDriver.Drive();
    }

    /// <summary>备入口：大厅与对局中由 HudManager 驱动。</summary>
    [HarmonyPatch]
    internal static class HudFramePatch
    {
        private static MethodBase TargetMethod() =>
            PatchHelper.FindByName(typeof(HudManager), "Update");

        private static void Postfix() => FrameDriver.Drive();
    }

    /// <summary>备入口：启动阶段由 SplashManager 驱动，保证开场动画能尽早播出。</summary>
    [HarmonyPatch]
    internal static class SplashFramePatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("SplashManager");
            return type == null ? null : PatchHelper.FindByName(type, "Update");
        }

        private static void Postfix() => FrameDriver.Drive();
    }
}
