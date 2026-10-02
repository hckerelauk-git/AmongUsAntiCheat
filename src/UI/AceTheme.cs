using UnityEngine;

namespace ApexCheatEnder.UI
{
    /// <summary>
    /// ACE（Apex Cheat Ender）视觉规范：配色、程序生成纹理、程序生成音效。
    ///
    /// 全部资源在代码里生成，不依赖任何外部图片/音频文件——
    /// 插件只需要一个 dll 就能带着完整视觉跑起来。
    /// </summary>
    internal static class AceTheme
    {
        // ================= 配色（深空 + 青色，与桌面启动动画同一套视觉） =================
        //
        // 改版说明：原配色是「企业蓝」（#2E9BFF 主色 + #1E6FD9 描边），
        // 和桌面启动动画的青色卡片放在一起像两个产品。
        // 现在统一到「深空底 + 青色强调」，整套 UI（桌面动画 / 面板 / 通知 / 设置）同源。

        /// <summary>窗口底色（深空蓝黑）。</summary>
        public static readonly Color WindowBg = new Color32(0x0A, 0x10, 0x18, 0xF0);

        /// <summary>面板底色。</summary>
        public static readonly Color PanelBg = new Color32(0x12, 0x1C, 0x2E, 0xE8);

        /// <summary>徽标 / 按钮底衬（比面板底色略亮，用于制造层次）。</summary>
        public static readonly Color BadgeBg = new Color32(0x18, 0x26, 0x3C, 0xFF);

        /// <summary>描边（低饱和，只做边界不抢视线）。</summary>
        public static readonly Color Border = new Color32(0x1E, 0x3A, 0x5F, 0xFF);

        /// <summary>主色（青）。</summary>
        public static readonly Color Primary = new Color32(0x2E, 0xE6, 0xD6, 0xFF);

        /// <summary>强调色（亮青，用于标题与高亮）。</summary>
        public static readonly Color Accent = new Color32(0x5A, 0xF0, 0xE0, 0xFF);

        /// <summary>主文字。</summary>
        public static readonly Color TextMain = new Color32(0xE8, 0xF2, 0xFF, 0xFF);

        /// <summary>次要文字。</summary>
        public static readonly Color TextDim = new Color32(0x7A, 0x8F, 0xA8, 0xFF);

        /// <summary>安全 / 正常。</summary>
        public static readonly Color Success = new Color32(0x3D, 0xD6, 0x8C, 0xFF);

        /// <summary>危险 / 作弊。</summary>
        public static readonly Color Danger = new Color32(0xFF, 0x4D, 0x4F, 0xFF);

        /// <summary>警告。</summary>
        public static readonly Color Warning = new Color32(0xFF, 0xB0, 0x2E, 0xFF);

        /// <summary>进度条底槽。</summary>
        public static readonly Color Track = new Color32(0x14, 0x2E, 0x4A, 0xFF);

        // ================= 设置界面专用（语义化，避免各处硬编码） =================

        /// <summary>左侧页签栏底色。</summary>
        public static readonly Color TabColumnBg = new Color32(0x0E, 0x18, 0x28, 0xF6);

        /// <summary>页签选中态底色。</summary>
        public static readonly Color TabActiveBg = new Color32(0x2E, 0xE6, 0xD6, 0x24);

        /// <summary>设置行底板（两种交替，制造斑马纹便于横向读行）。</summary>
        public static readonly Color RowBgA = new Color32(0x14, 0x20, 0x33, 0xC4);
        public static readonly Color RowBgB = new Color32(0x10, 0x1A, 0x2B, 0xC4);

        /// <summary>数字行的「−」按钮底色（暗色，表示减弱）。</summary>
        public static readonly Color BtnMinusBg = new Color32(0x1E, 0x3A, 0x5F, 0xE0);

        /// <summary>数字行的「+」按钮底色（青色，表示增强）。</summary>
        public static readonly Color BtnPlusBg = new Color32(0x2E, 0xE6, 0xD6, 0x9E);

        /// <summary>滚动条轨道 / 滑块。</summary>
        public static readonly Color ScrollTrack = new Color32(0x14, 0x20, 0x33, 0x80);
        public static readonly Color ScrollThumb = new Color32(0x2E, 0xE6, 0xD6, 0xB0);

        // ================= 纹理生成 =================

        /// <summary>生成 1x1 纯色纹理，用作 Image 的贴图。</summary>
        public static Texture2D MakeSolid(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 生成「圆角矩形 + 描边」的 **9 宫格 Sprite**。
        ///
        /// 为什么必须用 9 宫格：
        ///   uGUI 的 Image 直接拉伸一张圆角贴图，圆角会被拉成椭圆 —— 尺寸一变就露馅。
        ///   Sprite.Create 传 border 参数后，Unity 只拉伸中间区域、四个角保持原样，
        ///   于是一张 64×64 的小图能适配任意尺寸的卡片，圆角永远不变形。
        ///   这是「看起来做过设计」和「看起来是程序员拉的方块」之间最关键的一步。
        ///
        /// 尺寸约定：贴图边长 = radius*2 + 8，9 宫格边距 = radius + 1。
        /// </summary>
        /// <param name="radius">圆角半径（像素）。</param>
        /// <param name="borderWidth">描边宽度（像素）；传 0 表示无描边。</param>
        /// <param name="fill">填充色。</param>
        /// <param name="border">描边色；borderWidth 为 0 时忽略。</param>
        public static Sprite MakeCard(int radius, int borderWidth, Color fill, Color border)
        {
            if (radius < 1) radius = 1;
            var size = radius * 2 + 8;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            var transparent = new Color(0f, 0f, 0f, 0f);

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // 用像素中心点采样，避免边缘半像素误差导致锯齿
                    var px = x + 0.5f;
                    var py = y + 0.5f;

                    if (!InsideRoundedRect(px, py, 0f, 0f, size, size, radius))
                    {
                        pixels[y * size + x] = transparent;
                        continue;
                    }

                    // 内缩 borderWidth 的圆角矩形；落在它外面的部分就是描边
                    var isBorder = borderWidth > 0 &&
                                   !InsideRoundedRect(px, py,
                                       borderWidth, borderWidth,
                                       size - borderWidth * 2f, size - borderWidth * 2f,
                                       Mathf.Max(1f, radius - borderWidth));

                    pixels[y * size + x] = isBorder ? border : fill;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            var b = radius + 1f;
            return Sprite.Create(
                tex,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(b, b, b, b));   // ← 9 宫格边距，圆角不参与拉伸
        }

        /// <summary>
        /// 点是否落在圆角矩形内。
        /// 做法：把点钳制到「去掉圆角后的内矩形」上，再看它到钳制点的距离是否 ≤ 半径。
        /// 这是圆角矩形判定的经典写法，比逐个圆角判断省事且没有接缝。
        /// </summary>
        private static bool InsideRoundedRect(
            float x, float y, float left, float top, float w, float h, float r)
        {
            var minX = left + r;
            var maxX = left + w - r;
            var minY = top + r;
            var maxY = top + h - r;

            var cx = Mathf.Clamp(x, minX, maxX);
            var cy = Mathf.Clamp(y, minY, maxY);

            var dx = x - cx;
            var dy = y - cy;
            return dx * dx + dy * dy <= r * r;
        }

        /// <summary>把 9 宫格 Sprite 包成可直接赋给 Image 的缓存（按参数去重）。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, Sprite> CardCache
            = new System.Collections.Generic.Dictionary<string, Sprite>();

        public static Sprite Card(int radius, int borderWidth, Color fill, Color border)
        {
            var key = radius + "|" + borderWidth + "|" + ColorKey(fill) + "|" + ColorKey(border);
            if (CardCache.TryGetValue(key, out var cached) && cached != null) return cached;

            var sprite = MakeCard(radius, borderWidth, fill, border);
            CardCache[key] = sprite;
            return sprite;
        }

        private static string ColorKey(Color c) =>
            ((int)(c.r * 255)).ToString() + "," + ((int)(c.g * 255)).ToString() + "," +
            ((int)(c.b * 255)).ToString() + "," + ((int)(c.a * 255)).ToString();

        /// <summary>
        /// 程序生成盾牌图标。
        ///
        /// 形状：上半部分是矩形，下半部分是向下收窄的椭圆弧，
        /// 外圈描边、内部填充，形成经典的「安全盾」轮廓。
        /// </summary>
        /// <param name="size">纹理边长（像素）。</param>
        /// <param name="fill">内部填充色。</param>
        /// <param name="edge">描边色。</param>
        public static Texture2D MakeShield(int size, Color fill, Color edge)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            var transparent = new Color(0f, 0f, 0f, 0f);

            const float TopHalfWidth = 0.84f;   // 上半部分半宽（归一化）
            const float ShoulderY = 0.12f;      // 肩线位置
            const float EdgeThickness = 0.16f;  // 描边厚度

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // 归一化到 -1..1
                    var nx = (x + 0.5f) / size * 2f - 1f;
                    var ny = (y + 0.5f) / size * 2f - 1f;

                    float halfWidth;
                    if (ny >= ShoulderY)
                    {
                        halfWidth = TopHalfWidth;
                    }
                    else
                    {
                        // 下半部分：椭圆弧收窄
                        var t = (ShoulderY - ny) / (1f + ShoulderY);
                        halfWidth = TopHalfWidth * Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
                    }

                    var absX = Mathf.Abs(nx);
                    Color c;

                    if (absX > halfWidth)
                    {
                        c = transparent;
                    }
                    else
                    {
                        var nearSide = halfWidth - absX < EdgeThickness * halfWidth;
                        var nearTop = ny > 0.84f;
                        var nearBottom = ny < -0.86f;
                        c = (nearSide || nearTop || nearBottom) ? edge : fill;
                    }

                    pixels[y * size + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        // ================= 音效生成 =================

        /// <summary>
        /// 程序生成一段提示音。
        ///
        /// 用正弦波叠加指数衰减包络，做成类似「叮」的电子提示音，
        /// 避免为了一个音效就去打包外部音频资源。
        /// </summary>
        /// <param name="name">音频片段名。</param>
        /// <param name="startFreq">起始频率（Hz）。</param>
        /// <param name="endFreq">结束频率（Hz）。</param>
        /// <param name="duration">时长（秒）。</param>
        /// <param name="volume">音量 0~1。</param>
        public static AudioClip MakeTone(
            string name, float startFreq, float endFreq, float duration, float volume)
        {
            const int SampleRate = 44100;
            var sampleCount = Mathf.Max(1, (int)(SampleRate * duration));
            var data = new float[sampleCount];

            var phase = 0f;
            for (var i = 0; i < sampleCount; i++)
            {
                var t = i / (float)sampleCount;

                // 频率线性滑动
                var freq = Mathf.Lerp(startFreq, endFreq, t);
                phase += 2f * Mathf.PI * freq / SampleRate;

                // 指数衰减包络：起音干脆、尾部干净
                var envelope = Mathf.Exp(-4f * t) * Mathf.Min(1f, t * 40f);

                data[i] = Mathf.Sin(phase) * envelope * volume;
            }

            var clip = AudioClip.Create(name, sampleCount, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
