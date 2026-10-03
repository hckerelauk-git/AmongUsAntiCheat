using System;
using UnityEngine;
using UnityEngine.UI;

namespace ApexCheatEnder.UI
{
    /// <summary>
    /// ACE 界面根节点与帧调度。
    ///
    /// 职责：
    ///   1. 创建并持有唯一的 Canvas（ScreenSpaceOverlay，压在所有游戏 UI 之上）
    ///   2. 对外暴露 <see cref="DriveFrame"/>，由若干 Harmony 补丁共同调用
    ///   3. 做帧去重——多个驱动入口可能在同一帧都被触发，这里保证每帧只跑一次
    ///
    /// 关于帧驱动入口的选择：
    /// 最初挂在自定义 MonoBehaviour 的 Update 上，但动态注册进 il2cpp 的托管组件
    /// 收不到 Unity 每帧分发的消息（Awake 会触发，Update 不会）。
    /// 现在改为 patch 游戏/引擎侧的原生方法，并同时挂三个入口互为冗余：
    ///   - Canvas.SendWillRenderCanvases  每帧的静态方法，启动到退出一直存在（主）
    ///   - HudManager.Update              大厅 / 对局中（备）
    ///   - SplashManager.Update           启动阶段（备）
    /// </summary>
    internal static class AceUiRoot
    {
        private static bool _built;
        private static GameObject _canvasGo;
        private static bool _splashStarted;
        private static bool _desktopSplashShown;
        private static bool _firstFrameLogged;
        private static float _firstFrameTime;
        private static int _lastFrame = -1;

        /// <summary>桌面动画延迟启动时间（秒）。</summary>
        private const float DesktopSplashDelay = 1.5f;

        /// <summary>取得（必要时创建）UI 根节点。</summary>
        public static Transform EnsureCanvas()
        {
            if (_canvasGo != null) return _canvasGo.transform;
            if (_built) return null;

            _built = true;

            try
            {
                _canvasGo = new GameObject("ApexCheatEnderCanvas");
                UnityEngine.Object.DontDestroyOnLoad(_canvasGo);

                var canvas = _canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32000;   // 压在所有游戏 UI 之上

                var scaler = _canvasGo.AddComponent<CanvasScaler>();
                // 用 ConstantPixelSize 而不是 ScaleWithScreenSize：
                // 后者会按参考分辨率做缩放插值，非 1080p 屏幕上字体会明显发虚。
                // 这里让 UI 按物理像素 1:1 渲染，文字最锐利。
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;
                scaler.referencePixelsPerUnit = 100f;

                // 不需要接收点击，关掉射线检测避免遮挡游戏操作
                var raycaster = _canvasGo.AddComponent<GraphicRaycaster>();
                raycaster.enabled = false;

                AntiCheatRuntime.Log?.LogInfo("[UI] 画布已创建。");
                return _canvasGo.transform;
            }
            catch (Exception ex)
            {
                AntiCheatRuntime.Log?.LogError($"[UI] 创建画布失败：{ex}");
                return null;
            }
        }

        /// <summary>
        /// 统一帧入口。多个补丁入口都可能调用它，内部做帧去重。
        /// </summary>
        public static void DriveFrame()
        {
            var frame = Time.frameCount;
            if (frame == _lastFrame) return;
            _lastFrame = frame;

            var now = Time.time;

            // 检测循环
            AntiCheatRuntime.Tick(now, Time.deltaTime);

            // 界面
            try
            {
                var root = EnsureCanvas();
                if (root == null) return;

                MainMenuArt.Tick();
                SettingsWindow.EnsureBuilt(root);
                NotificationPanel.EnsureBuilt(root);

                // 桌面右下角启动动画只播一次。
                // 延迟到游戏跑起来之后再弹：桌面窗口走的是原生消息循环，
                // 等游戏初始化稳定后再启动，能把风险隔离在启动阶段之外。
                if (!_splashStarted)
                {
                    _splashStarted = true;
                    _firstFrameTime = now;
                }

                if (!_desktopSplashShown && now - _firstFrameTime >= DesktopSplashDelay)
                {
                    _desktopSplashShown = true;
                    if (AntiCheatRuntime.Config?.ShowDesktopSplash.Value ?? true)
                    {
                        AntiCheatRuntime.Log?.LogInfo("[UI] 启动桌面右下角动画...");
                        DesktopSplash.Show();
                    }
                }

                SettingsWindow.Tick(Input.GetKeyDown(KeyCode.Insert));
                NotificationPanel.Tick();
                ChatAbuseNotice.Tick(root);

                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    AntiCheatRuntime.Log?.LogInfo("[UI] 首帧渲染完成。");
                }
            }
            catch (Exception ex)
            {
                AntiCheatRuntime.Log?.LogWarning($"[UI] 帧刷新异常：{ex.Message}");
            }
        }
    }
}
