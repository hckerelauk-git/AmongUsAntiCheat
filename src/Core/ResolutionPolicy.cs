namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 分辨率是否合理的纯判定。
    ///
    /// 单独抽出来是因为 <see cref="ResolutionGuard"/> 要调 Unity 的
    /// <c>Screen</c> / <c>Time</c>，没法编进测试工程；判定逻辑必须能脱离 Unity 回归。
    /// </summary>
    internal static class ResolutionPolicy
    {
        /// <summary>小于这个宽度就认为窗口不对劲。</summary>
        public const int MinSaneWidth = 800;

        /// <summary>小于这个高度就认为窗口不对劲。</summary>
        public const int MinSaneHeight = 480;

        /// <summary>默认修复到的宽度。</summary>
        public const int DefaultFixWidth = 1280;

        /// <summary>默认修复到的高度。</summary>
        public const int DefaultFixHeight = 600;

        /// <summary>
        /// 尺寸是否不合理、需要修复。
        ///
        /// 实测过的坏值：144×1（客户区 1 像素高，窗口 160×40，基本看不见）。
        /// 阈值取 800×480 —— 比这还小的窗口在任何显示器上都没法正常玩。
        /// </summary>
        public static bool NeedsRepair(int width, int height) =>
            width < MinSaneWidth || height < MinSaneHeight;

        /// <summary>修复目标是否本身合理（避免被配成更小的值反而更糟）。</summary>
        public static bool IsValidTarget(int width, int height) =>
            width >= MinSaneWidth && height >= MinSaneHeight;
    }
}
