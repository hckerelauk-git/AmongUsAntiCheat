using System;

namespace ApexCheatEnder.Core
{
    // 内嵌图片资源与纯托管裁切计算，不含网络访问。
    internal static class MenuArtSource
    {
        /// <summary>
        /// 随机池里的内嵌图片资源名。
        ///
        /// 加图 / 删图只改这里和 csproj 的 EmbeddedResource —— 不再用
        /// First/Second/Third 这种写死的常量名，删一张不会留下断号。
        /// </summary>
        internal static readonly string[] PoolResources =
        {
            "ApexCheatEnder.MainMenuArt01.jpg",
            "ApexCheatEnder.MainMenuArt03.jpg",
            "ApexCheatEnder.MainMenuArt04.jpg",
            "ApexCheatEnder.MainMenuArt05.jpg",
            "ApexCheatEnder.MainMenuArt06.jpg",
        };

        /// <summary>旧兜底图，不进入随机池。</summary>
        internal const string FallbackResource = "ApexCheatEnder.MainMenuArt.jpg";

        /// <summary>随机池里的图片数量。</summary>
        internal static int BackgroundCount => PoolResources.Length;

        /// <summary>
        /// 从池里选一张，保证与上一次不同。
        ///
        /// previous 越界（首次选择）时直接随机；否则在「除去 previous 的其余项」里取，
        /// 这样连续两次启动不会看到同一张。
        /// </summary>
        internal static int SelectBackground(int previous, Random random)
        {
            var count = BackgroundCount;
            if (count <= 0) return 0;
            if (previous < 0 || previous >= count) return random.Next(count);
            if (count == 1) return 0;

            var next = random.Next(count - 1);
            return next >= previous ? next + 1 : next;
        }

        internal static (float X, float Y, float Width, float Height) Crop(float width, float height, float aspect)
        {
            if (width <= 0 || height <= 0 || aspect <= 0 || float.IsNaN(aspect) || float.IsInfinity(aspect))
                throw new ArgumentOutOfRangeException(nameof(aspect));
            var w = Math.Min(width, height * aspect);
            var h = Math.Min(height, width / aspect);
            return ((width - w) / 2f, (height - h) / 2f, w, h);
        }
    }
}
