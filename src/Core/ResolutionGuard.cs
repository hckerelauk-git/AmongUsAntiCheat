using UnityEngine;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 分辨率自愈：检测到窗口被压成不合理的小尺寸时，自动改回可用分辨率。
    ///
    /// ────────────── 为什么需要这个 ──────────────
    ///
    /// 实际出现过游戏窗口变为 160×40（客户区 144×1，1 像素高）的情况。
    /// 它是个**自锁死循环**：
    ///
    ///   Unity 把「当前窗口客户区尺寸」当作分辨率存进注册表
    ///     → 下次启动照这个值还原窗口
    ///     → 窗口又只有 144×1
    ///     → Unity 再存一次 144×1
    ///
    /// 手动把窗口拉大一次可以临时解决，但只要有一次被压小，之后每次启动都会复现。
    /// 所以在插件里加一道守卫：**发现尺寸不合理就自己改回来**，打断这个循环。
    ///
    /// 用 Unity 自己的 <c>Screen.SetResolution</c> 而不是直接写注册表 ——
    /// 那是游戏自己的设置存储，插件不该越权去改；走 Unity API 改完之后
    /// Unity 会自己把正确值存回去，闭环自然就正了。
    /// </summary>
    internal static class ResolutionGuard
    {
        /// <summary>检查间隔（秒）。启动阶段每秒看一次就够。</summary>
        private const float CheckInterval = 1f;

        /// <summary>
        /// 最多修几次。
        ///
        /// 必须有上限：如果改了也无效（比如被别的东西反复压小），
        /// 无限重试只会每秒刷一条日志。
        /// </summary>
        private const int MaxRepairAttempts = 5;

        private static float _nextCheck;
        private static int _repairs;

        public static void Reset() => _repairs = 0;

        /// <summary>由帧驱动调用。任何时候都能安全调用。</summary>
        public static void Tick()
        {
            var cfg = AntiCheatRuntime.Config;
            if (cfg == null) return;
            if (!(cfg.AutoFixResolution?.Value ?? true)) return;
            if (_repairs >= MaxRepairAttempts) return;

            var now = Time.unscaledTime;
            if (now < _nextCheck) return;
            _nextCheck = now + CheckInterval;

            int width, height;
            try
            {
                width = Screen.width;
                height = Screen.height;
            }
            catch
            {
                return;
            }

            if (!ResolutionPolicy.NeedsRepair(width, height)) return;

            var targetWidth = cfg.FixResolutionWidth?.Value ?? ResolutionPolicy.DefaultFixWidth;
            var targetHeight = cfg.FixResolutionHeight?.Value ?? ResolutionPolicy.DefaultFixHeight;
            if (!ResolutionPolicy.IsValidTarget(targetWidth, targetHeight)) return;

            _repairs++;

            AntiCheatRuntime.Log?.LogWarning(
                "[分辨率] 检测到窗口尺寸异常：" + width + "x" + height +
                "，自动改回 " + targetWidth + "x" + targetHeight + "（第 " + _repairs + " 次）。");

            try
            {
                // 用 bool 重载：interop 里最稳的一个。
                // 走 Unity API 改完之后 Unity 会把正确值存回它自己的设置，
                // 下次启动就不会再还原成小窗口了。
                Screen.SetResolution(targetWidth, targetHeight, false);
            }
            catch (System.Exception ex)
            {
                AntiCheatRuntime.Log?.LogWarning("[分辨率] 自动修改失败：" + ex.Message);
            }
        }
    }
}
