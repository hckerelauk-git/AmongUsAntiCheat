using System;
using System.Reflection;
using ApexCheatEnder.Config;
using ApexCheatEnder.Core;
using ApexCheatEnder.Patches;
using ApexCheatEnder.UI;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ApexCheatEnder
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
        public const string PluginVersion = "1.1.1";

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
            _harmony = new Harmony(PluginGuid);
            TryPatch(typeof(CanvasFramePatch), "帧驱动(Canvas)");
            TryPatch(typeof(HudFramePatch), "帧驱动(HudManager)");
            TryPatch(typeof(SplashFramePatch), "帧驱动(SplashManager)");
            TryPatch(typeof(MainMenuArtStartPatch), "主菜单背景(Start)");
            TryPatch(typeof(ChatNoticePatch), "本地聊天提示(AddChat)");
            if (cfg.EnableEventScan.Value)
                ApplyPatches();
            else
                Log.LogInfo("[初始化] 事件检测已关闭；界面与主菜单背景入口保持挂载。");

            Log.LogInfo("Apex Cheat Ender 加载完成。按 Insert 打开设置。");
        }

        /// <summary>
        /// 逐个挂载补丁，单个失败不影响其余。
        /// 这在游戏更新后尤其重要——某个方法被改名，不应该导致整套检测瘫痪。
        /// </summary>
        private void ApplyPatches()
        {
            TryPatch(typeof(MurderPlayerPatch), "击杀");
            TryPatch(typeof(CompleteTaskPatch), "任务完成");
            TryPatch(typeof(VentEnterPatch), "通风管");
            TryPatch(typeof(SnapToPatch), "位置强制同步");

            // ---------- 进阶检测（本次新增） ----------
            // RPC 上下文必须先挂：UpdateSystem 这类方法签名里没有玩家信息，
            // 只能靠它记录的发送者来归属证据。
            TryPatch(typeof(RpcContextPatch), "RPC 上下文");

            TryPatch(typeof(UpdateSystemPatch), "破坏系统");
            TryPatch(typeof(ReportDeadBodyPatch), "报告尸体 / 早会");
            TryPatch(typeof(SendChatPatch), "聊天");
            TryPatch(typeof(SetNamePatch), "昵称");
            TryPatch(typeof(ShapeshiftPatch), "变形");
            TryPatch(typeof(ProtectPatch), "保护");
            TryPatch(typeof(VentOpPatch), "通风管操作");
            TryPatch(typeof(BootFromVentPatch), "强制踢出通风管");
            TryPatch(typeof(ZiplinePatch), "滑索");
            TryPatch(typeof(OversizedPacketPatch), "超大数据包");

            // RPC 洪水防护：唯一的 Prefix 补丁——它是唯一能在 RPC 执行前
            // 把包丢掉的一层，之前因为 TryPatch 只认 Postfix 而从未被挂载。
            TryPatch(typeof(RpcFloodPatches), "RPC 洪水防护");

            // ACE 客户端互认：认出房间里同样装了本插件的人，并在其名字上加标记
            TryPatch(typeof(AcePresenceRpcPatch), "ACE 互认(握手)");
            TryPatch(typeof(AcePresenceNamePatch), "ACE 互认(名字标记)");
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

                // Prefix 与 Postfix 都支持：Prefix 用于「拦截」（可返回 false 丢弃 RPC），
                // Postfix 用于「观察」（事后记录证据）。至少要有其一。
                var prefix = patchType.GetMethod("Prefix", Flags);
                var postfix = patchType.GetMethod("Postfix", Flags);

                if (prefix == null && postfix == null)
                {
                    Log.LogWarning($"[补丁] {label}：补丁类既未实现 Prefix 也未实现 Postfix，已跳过。");
                    return;
                }

                _harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null);

                var kind = prefix != null
                    ? (postfix != null ? "Prefix+Postfix" : "Prefix")
                    : "Postfix";

                Log.LogInfo($"[补丁] {label} 已挂载 -> {target.DeclaringType?.Name}.{target.Name}"
                          + $"({target.GetParameters().Length} 参数, {kind})");
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
