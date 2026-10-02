using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace AmongUsAntiCheat.UI
{
    /// <summary>
    /// 主菜单背景替换。
    ///
    /// 做法：把内嵌的图片解码成 Texture2D → Sprite，然后替换主菜单里那个
    /// 背景 SpriteRenderer 的 sprite。不改任何预制体、不写盘，纯运行时替换。
    ///
    /// 为什么用内嵌资源而不是外部图片文件：
    /// 插件的卖点就是「丢一个 dll 就能用」。要求用户额外拷贝一张图，
    /// 就会出现「图丢了 → 背景变白」这种低级故障。
    ///
    /// 为什么要在 Update 里反复应用：
    /// 游戏在切场景、开关菜单时会重建主菜单对象，替换过的 sprite 会被还原。
    /// 所以除了首次应用，还需要低频巡检补刀（有节流，开销可忽略）。
    /// </summary>
    internal static class MainMenuArt
    {
        /// <summary>内嵌资源名（与 csproj 里的 LogicalName 一致）。</summary>
        private const string ResourceName = "AmongUsAntiCheat.MainMenuArt.jpg";

        /// <summary>背景对象可能的命名。按优先级排列。</summary>
        private static readonly string[] BackgroundNames = { "Background", "BackgroundImage", "Bg" };

        private static Sprite _sprite;
        private static bool _loadFailed;
        private static bool _logged;

        /// <summary>巡检间隔（秒）。主菜单不需要每帧检查。</summary>
        private const float RecheckInterval = 0.5f;
        private static float _nextCheckTime;

        /// <summary>
        /// 把主菜单背景换成自定义图。由补丁在 Start / Update 里调用。
        /// </summary>
        /// <param name="menuRoot">主菜单根节点；为 null 时跳过。</param>
        public static void Apply(Transform menuRoot)
        {
            if (menuRoot == null) return;

            // 节流放在最前面：本方法由 Update 补丁每帧调用，
            // 配置读取和层级遍历都必须挡在节流之后，否则等于每帧白跑。
            var now = Time.time;
            if (now < _nextCheckTime) return;
            _nextCheckTime = now + RecheckInterval;

            if (!(AntiCheatRuntime.Config?.ShowMainMenuArt.Value ?? true)) return;

            try
            {
                var sprite = GetSprite();
                if (sprite == null) return;

                var renderer = FindBackgroundRenderer(menuRoot);
                if (renderer == null) return;

                // 已经是我们的图就不重复赋值（Sprite 比较是引用比较，很便宜）
                if (renderer.sprite == sprite) return;

                renderer.sprite = sprite;

                if (!_logged)
                {
                    _logged = true;
                    AntiCheatRuntime.Log?.LogInfo($"[主菜单] 背景已替换 -> {renderer.gameObject.name}");
                }
            }
            catch (Exception ex)
            {
                if (!_logged)
                {
                    _logged = true;
                    AntiCheatRuntime.Log?.LogWarning($"[主菜单] 替换背景失败：{ex.Message}");
                }
            }
        }

        /// <summary>把内嵌图片解码成 Sprite（只做一次，失败也不再重试）。</summary>
        private static Sprite GetSprite()
        {
            if (_sprite != null) return _sprite;
            if (_loadFailed) return null;

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    _loadFailed = true;
                    AntiCheatRuntime.Log?.LogWarning(
                        $"[主菜单] 找不到内嵌资源 {ResourceName}，跳过背景替换。");
                    return null;
                }

                var bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }

                // mipChain=false：背景是 2D 全屏绘制，不需要 mipmap，省显存。
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes))
                {
                    _loadFailed = true;
                    AntiCheatRuntime.Log?.LogWarning("[主菜单] 图片解码失败，跳过背景替换。");
                    return null;
                }

                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;

                _sprite = Sprite.Create(
                    tex,
                    new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f),
                    100f);

                AntiCheatRuntime.Log?.LogInfo($"[主菜单] 背景图已解码：{tex.width}x{tex.height}");
                return _sprite;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                AntiCheatRuntime.Log?.LogWarning($"[主菜单] 加载背景图异常：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 在主菜单层级里找背景渲染器。
        ///
        /// 先按名字精确匹配；找不到就退回「层级里第一个 SpriteRenderer」——
        /// 因为不同游戏版本背景对象的命名并不固定，宁可换个不那么准的，
        /// 也好过什么都不做。
        /// </summary>
        private static SpriteRenderer FindBackgroundRenderer(Transform menuRoot)
        {
            SpriteRenderer firstAny = null;

            var stack = new Stack<Transform>();
            stack.Push(menuRoot);

            // 限制遍历规模：主菜单层级不深，超过上限说明找错了根节点。
            var guard = 0;
            while (stack.Count > 0 && guard++ < 2000)
            {
                var t = stack.Pop();
                if (t == null) continue;

                var go = t.gameObject;
                if (go != null)
                {
                    var sr = go.GetComponent<SpriteRenderer>();
                    if (sr != null)
                    {
                        var n = go.name ?? string.Empty;
                        foreach (var want in BackgroundNames)
                        {
                            if (n.Equals(want, StringComparison.OrdinalIgnoreCase)) return sr;
                        }
                        if (firstAny == null) firstAny = sr;
                    }
                }

                var count = t.childCount;
                for (var i = 0; i < count; i++) stack.Push(t.GetChild(i));
            }

            return firstAny;
        }
    }
}
