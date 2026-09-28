using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AmongUsAntiCheat.UI
{
    /// <summary>
    /// uGUI 元素构建工具。
    ///
    /// 全部用 il2cpp 原生类型（RectTransform / Image / Text），
    /// 不使用自定义 MonoBehaviour，也不依赖任何预制体资源。
    /// </summary>
    internal static class UiBuilder
    {
        /// <summary>纹理 -> Sprite 的缓存，避免重复创建。</summary>
        private static readonly Dictionary<int, Sprite> SpriteCache = new Dictionary<int, Sprite>();

        /// <summary>从纹理取 Sprite（带缓存）。</summary>
        public static Sprite ToSprite(Texture2D texture)
        {
            if (texture == null) return null;

            var key = texture.GetInstanceID();
            if (SpriteCache.TryGetValue(key, out var cached) && cached != null) return cached;

            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);

            SpriteCache[key] = sprite;
            return sprite;
        }

        /// <summary>创建一个带 RectTransform 的空节点。</summary>
        public static GameObject CreateNode(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        /// <summary>创建一个纯色/贴图 Image。</summary>
        public static Image CreateImage(string name, Transform parent, Color color, Texture2D texture = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            if (texture != null) image.sprite = ToSprite(texture);

            return image;
        }

        /// <summary>创建一段文本。</summary>
        public static Text CreateText(
            string name,
            Transform parent,
            string content,
            Font font,
            int fontSize,
            Color color,
            TextAnchor anchor = TextAnchor.UpperLeft,
            FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var text = go.AddComponent<Text>();
            text.text = content;
            text.font = font;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = anchor;
            text.fontStyle = style;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            return text;
        }

        /// <summary>
        /// 按锚点定位一个元素。
        /// </summary>
        /// <param name="rect">目标 RectTransform。</param>
        /// <param name="anchor">锚点（0~1，0~1）。</param>
        /// <param name="pivot">轴心（0~1，0~1）。</param>
        /// <param name="anchoredPosition">相对锚点的偏移。</param>
        /// <param name="size">尺寸；传 (0,0) 表示拉伸填满父级。</param>
        public static void Place(
            RectTransform rect,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 size)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;

            if (size.x > 0f || size.y > 0f)
            {
                rect.sizeDelta = size;
            }
            else
            {
                // 拉伸填满父级
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
        }

        /// <summary>让元素拉伸填满父级，并留出内边距。</summary>
        public static void Stretch(RectTransform rect, float left, float top, float right, float bottom)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>
        /// 加载能显示中文的字体。
        /// Unity 内置字体不含汉字，必须从系统字体创建动态字体，否则满屏方块。
        ///
        /// 结果做了缓存：这个调用有开销，而界面上每个 Text 都要用它。
        /// </summary>
        private static Font _cachedFont;
        private static bool _fontResolved;

        public static Font LoadFont(int size)
        {
            if (_fontResolved && _cachedFont != null) return _cachedFont;

            try
            {
                var font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei", "微软雅黑", "SimHei", "SimSun", "Arial Unicode MS" },
                    size);
                if (font != null)
                {
                    _cachedFont = font;
                    _fontResolved = true;
                    return font;
                }
            }
            catch { }

            // 兜底：Unity 内置字体（英文可显示，中文会变方块，但至少不会让 Text 崩）
            try
            {
                var fallback = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                            ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (fallback != null)
                {
                    _cachedFont = fallback;
                    _fontResolved = true;
                    return fallback;
                }
            }
            catch { }

            return null;
        }
    }
}
