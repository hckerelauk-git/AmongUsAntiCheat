using UnityEngine;

namespace AmongUsAntiCheat.UI
{
    /// <summary>
    /// ACE（Apex Cheat Ender）视觉规范：配色、程序生成纹理、程序生成音效。
    ///
    /// 全部资源在代码里生成，不依赖任何外部图片/音频文件——
    /// 插件只需要一个 dll 就能带着完整视觉跑起来。
    /// </summary>
    internal static class AceTheme
    {
        // ================= 配色（深蓝科技感） =================

        /// <summary>窗口底色。</summary>
        public static readonly Color WindowBg = new Color32(0x08, 0x12, 0x1E, 0xF0);

        /// <summary>面板底色。</summary>
        public static readonly Color PanelBg = new Color32(0x0E, 0x1C, 0x2B, 0xE8);

        /// <summary>主边框蓝。</summary>
        public static readonly Color Border = new Color32(0x1E, 0x6F, 0xD9, 0xFF);

        /// <summary>主色。</summary>
        public static readonly Color Primary = new Color32(0x2E, 0x9B, 0xFF, 0xFF);

        /// <summary>强调青。</summary>
        public static readonly Color Accent = new Color32(0x00, 0xD9, 0xFF, 0xFF);

        /// <summary>主文字。</summary>
        public static readonly Color TextMain = new Color32(0xE6, 0xF1, 0xFF, 0xFF);

        /// <summary>次要文字。</summary>
        public static readonly Color TextDim = new Color32(0x7A, 0x93, 0xAD, 0xFF);

        /// <summary>安全 / 正常。</summary>
        public static readonly Color Success = new Color32(0x3D, 0xD6, 0x8C, 0xFF);

        /// <summary>危险 / 作弊。</summary>
        public static readonly Color Danger = new Color32(0xFF, 0x4D, 0x4F, 0xFF);

        /// <summary>警告。</summary>
        public static readonly Color Warning = new Color32(0xFF, 0xB0, 0x2E, 0xFF);

        /// <summary>进度条底槽。</summary>
        public static readonly Color Track = new Color32(0x14, 0x28, 0x3C, 0xFF);

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
