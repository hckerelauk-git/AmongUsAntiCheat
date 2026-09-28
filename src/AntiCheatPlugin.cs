using System;
using System.Reflection;
using AmongUsAntiCheat.Config;
using AmongUsAntiCheat.Core;
using AmongUsAntiCheat.Patches;
using AmongUsAntiCheat.UI;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace AmongUsAntiCheat
{
    /// <summary>
    /// 插件入口。
    ///
    /// 装配顺序有讲究：先建好各检测模块并挂上检测循环，最后才打补丁。
    /// 因为补丁一旦挂上，游戏里随时可能触发回调，此时如果检测循环还没就绪，
    /// 回调就会拿到 null 而丢事件。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Among Us.exe")]
    public sealed class AntiCheatPlugin : BasePlugin
    {
        public const string PluginGuid = "apex.cheat.ender";
        public const string PluginName = "ApexCheatEnder";
        public const string PluginVersion = "1.1.0";

        /// <summary>供补丁层写日志的全局入口。</summary>
        internal static ManualLogSource LogSource;

        /// <summary>事件检测开关的只读快照，避免补丁里反复读配置。</summary>
        internal static bool EventScanEnabled = true;

        private Harmony _harmony;

        public override void Load()
        {
            LogSource = Log;

            Log.LogInfo("========================================");
            Log.LogInfo($"  Apex Cheat Ender  v{PluginVersion}");
            Log.LogInfo("  Among Us 客户端反作弊");
            Log.LogInfo("========================================");

            // ---------- 配置 ----------
            var cfg = new AntiCheatConfig(Config);
            EventScanEnabled = cfg.EnableEventScan.Value;

            // ---------- 检测模块 ----------
            var tracker = new PlayerTracker();
            var analyzer = new BehaviorAnalyzer(cfg, Log);
            var verdicts = new VerdictEngine(cfg, Log);
            var scanner = new ModScanner(cfg, Log);

            // ---------- 运行时容器 ----------
            // 所有依赖注入纯托管容器。检测循环由 Harmony 补丁驱动，
            // 刻意不使用自定义 MonoBehaviour —— 动态注册进 il2cpp 的托管组件
            // 收不到 Unity 每帧分发的消息（Awake 会触发，Update/OnGUI 不会）。
            AntiCheatRuntime.Initialize(cfg, Log, tracker, analyzer, verdicts, scanner);
            AntiCheatRuntime.OnLoaded();

            Log.LogInfo($"[初始化] 采样间隔 {cfg.SampleInterval.Value:F2}s，"
                      + $"判定方式：规则命中（无分数累积），"
                      + $"自动踢人 {(cfg.AllowAutoKick.Value ? "开启" : "关闭")}。");

            // ---------- 补丁 ----------
            if (cfg.EnableEventScan.Value)
                ApplyPatches();
            else
                Log.LogInfo("[初始化] 事件检测已关闭，跳过补丁挂载。");

            Log.LogInfo("Apex Cheat Ender 加载完成。按 F8 可切换监控面板。");
        }

        /// <summary>
        /// 逐个挂载补丁，单个失败不影响其余。
        /// 这在游戏更新后尤其重要——某个方法被改名，不应该导致整套检测瘫痪。
        /// </summary>
        private void ApplyPatches()
        {
            _harmony = new Harmony(PluginGuid);

            // 帧驱动必须最先挂载：它是整个检测循环与界面的心跳。
            // 三个入口互为冗余，任一可用即可（详见 FrameDriverPatch）。
            TryPatch(typeof(CanvasFramePatch), "帧驱动(Canvas)");
            TryPatch(typeof(HudFramePatch), "帧驱动(HudManager)");
            TryPatch(typeof(SplashFramePatch), "帧驱动(SplashManager)");

            TryPatch(typeof(MurderPlayerPatch), "击杀");
            TryPatch(typeof(CompleteTaskPatch), "任务完成");
            TryPatch(typeof(VentEnterPatch), "通风管");
            TryPatch(typeof(SnapToPatch), "位置强制同步");
        }

        private void TryPatch(Type patchType, string label)
        {
            const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;

            try
            {
                var targetMethodInfo = patchType.GetMethod("TargetMethod", Flags);
                if (targetMethodInfo == null)
                {
                    Log.LogWarning($"[补丁] {label}：补丁类未实现 TargetMethod，已跳过。");
                    return;
                }

                var target = targetMethodInfo.Invoke(null, null) as MethodBase;
                if (target == null)
                {
                    Log.LogWarning($"[补丁] {label}：在当前游戏版本中未找到目标方法，已跳过该检测。");
                    return;
                }

                var postfix = patchType.GetMethod("Postfix", Flags);
                if (postfix == null)
                {
                    Log.LogWarning($"[补丁] {label}：补丁类未实现 Postfix，已跳过。");
                    return;
                }

                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));

                Log.LogInfo($"[补丁] {label} 已挂载 -> {target.DeclaringType?.Name}.{target.Name}"
                          + $"({target.GetParameters().Length} 参数)");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[补丁] {label} 挂载失败（该检测将不可用）：{ex.Message}");
            }
        }

        public override bool Unload()
        {
            try
            {
                _harmony?.UnpatchSelf();
                SnapToPatch.ResetStats();
                AntiCheatRuntime.Shutdown();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[卸载] 还原补丁时异常：{ex.Message}");
            }
            return true;
        }
    }
}
