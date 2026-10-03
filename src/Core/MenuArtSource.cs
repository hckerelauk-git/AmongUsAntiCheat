using System;

namespace ApexCheatEnder.Core
{
    // 内嵌图片资源与纯托管裁切计算，不含网络访问。
    internal static class MenuArtSource
    {
        internal const string FirstResource = "ApexCheatEnder.MainMenuArt01.jpg";
        internal const string SecondResource = "ApexCheatEnder.MainMenuArt02.jpg";
        internal const string FallbackResource = "ApexCheatEnder.MainMenuArt.jpg";

        internal const string ThirdResource = "ApexCheatEnder.MainMenuArt03.jpg";
        internal const int BackgroundCount = 3;

        // 从其余两张中随机选择，旧兜底图不进入随机池。
        internal static int SelectBackground(int previous, Random random)
        {
            if (previous < 0 || previous >= BackgroundCount) return random.Next(BackgroundCount);
            var next = random.Next(BackgroundCount - 1);
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
