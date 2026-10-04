using System;
using System.IO;
using System.Reflection;
using ApexCheatEnder.Core;
using UnityEngine;

namespace ApexCheatEnder.UI
{
    /// <summary>
    /// 主菜单背景：每次启动从内嵌图里随机选一张铺在主菜单上。
    ///
    /// ══════════════════════════════════════════════════════════════
    ///  本文件是 Amethyst 的 `AmethystMainMenuArt` 的**逐字移植**。
    ///
    ///  为什么强调「逐字」：这套做法里每一个看似随意的细节都有作用，
    ///  我自己「改进」过的每一处都导致过线上问题：
    ///
    ///   1. **写死世界坐标 (0,0,600)，不要跟随相机。**
    ///      我改成「相机正前方」后，图开始偏移、被背景压住。
    ///      ame 的 `FitToScreen` 只负责缩放，位置从头到尾就是 (0,0,600)。
    ///
    ///   2. **贴图和精灵必须设 hideFlags |= 0x3D。**
    ///      这是我漏掉的一行。0x3D 含 DontUnloadUnusedAsset ——
    ///      不设的话 Unity 的资源清理会把贴图当垃圾回收，精灵变空，背景随机消失。
    ///
    ///   3. **尺寸只用 Camera.main 的正交尺寸与宽高比算。**
    ///      我自作聪明改成「取所有启用相机的较大者 × 安全系数」，
    ///      结果反而盖住 UI。ame 就是 `orthographicSize * 2` 与 `* aspect`。
    ///
    ///   4. **不设 sortingOrder。** 保持默认 0。抬高会盖住游戏 UI（实测）。
    ///
    ///   5. **必须关掉原版氛围对象 `Ambience`**，否则它会盖在自定义图上。
    ///
    ///  结论：想「改进」ame 的写法之前，先想清楚它为什么那么写。
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    internal static class MainMenuArt
    {
        private static readonly string[] ResourceNames = MenuArtSource.PoolResources;

        /// <summary>进程内只选一次；离开菜单再回来、卸载清理都不重新抽图。</summary>
        private static readonly int SelectedBackground = new System.Random().Next(MenuArtSource.BackgroundCount);

        /// <summary>与 Amethyst 一致：150 像素/单位。</summary>
        private const float PixelsPerUnit = 150f;

        /// <summary>
        /// 摆放位置。**写死的世界坐标，与 Amethyst 完全一致。**
        ///
        /// 不要改成「相机正前方」—— 试过，图会偏移并被背景压住。
        /// 这套做法成立的前提就是相机在原点附近、朝 +Z，本机满足。
        /// </summary>
        private static readonly Vector3 Position = new Vector3(0f, 0f, 600f);

        /// <summary>原版氛围对象的名字。与 Amethyst 用的是同一个。</summary>
        private const string AmbienceName = "Ambience";

        /// <summary>
        /// 需要**整体隐藏**的原版对象。**这是让背景图真正露出来的关键。**
        ///
        /// 来自 Amethyst 的 `AmethystMainMenuOptimize.Tick`。我先前只关 `Ambience`，
        /// 完全不知道还有 `BackgroundTexture` —— 那才是游戏真正的背景贴图，
        /// 它一直盖在自定义图上，所以图只在半透明面板里透出来。
        ///
        /// **只留这一个。**
        /// Amethyst 还会一并隐藏 `WindowShine` / `MainUI/Tint` / `MaskedBlackScreen`
        /// 并关掉 `LeftPanel` / `RightPanel` 的底板，但那是为了配合它自己重绘的界面。
        /// 我们只要换背景，不需要动这些 —— 实测隐藏它们会导致主菜单「开始」按钮点不动，
        /// 因为其中有些对象和菜单的交互流程绑在一起。
        ///
        /// 少动一个原版对象，就少一份弄坏菜单的风险。
        /// </summary>
        private static readonly string[] HideObjectPaths =
        {
            "BackgroundTexture",
        };

        /// <summary>
        /// 需要**关掉 SpriteRenderer** 的原版对象。
        ///
        /// `RightPanel` 是右侧好友面板的底板，它那块带粗边框的圆角外框会盖在背景图上 ——
        /// Amethyst 同样对它做 `HideRenderer`。关掉后外框消失，背景从后面透出来。
        ///
        /// **只加这一个。** `LeftPanel` 暂时不动：左侧按钮区的外框观感尚可，
        /// 而它和菜单交互流程的关联更可疑，能不碰就不碰。
        /// </summary>
        private static readonly string[] HideRendererPaths =
        {
            "RightPanel",
        };

        /// <summary>被我们隐藏 / 关掉的原版对象，离开菜单时逐个恢复。</summary>
        private static readonly System.Collections.Generic.List<GameObject> HiddenObjects =
            new System.Collections.Generic.List<GameObject>(8);

        private static readonly System.Collections.Generic.List<SpriteRenderer> DisabledRenderers =
            new System.Collections.Generic.List<SpriteRenderer>(4);

        /// <summary>连续失败多少次后彻底放弃，避免刷屏。</summary>
        private const int MaxFailures = 3;

        private static Transform _menuRoot;
        private static GameObject _art;
        private static Sprite _sprite;
        private static Texture2D _texture;

        private static GameObject _ambience;
        private static bool _ambienceHidden;
        private static float _nextAmbienceFind;

        private static float _nextCheck;
        private static int _failures;
        private static bool _gaveUp;
        private static bool _logged;

        public static void Apply(Transform menuRoot)
        {
            if (menuRoot == null) return;
            if (_menuRoot != menuRoot)
            {
                Destroy();
                _menuRoot = menuRoot;
                _nextCheck = 0f;
                _failures = 0;
                _gaveUp = false;
                _logged = false;
                _nextAmbienceFind = 0f;
            }
            Tick();
        }

        /// <summary>由已有 Canvas 主线程帧入口驱动，不依赖游戏存在 Update。</summary>
        public static void Tick()
        {
            if (_gaveUp) return;
            if (_menuRoot == null) return;

            // 菜单暂时隐藏：先还原，但**保留引用** —— 菜单可能只是被临时关掉，
            // 回来时要能重新接管。
            if (!_menuRoot.gameObject.activeInHierarchy)
            {
                if (_art != null) Destroy();
                return;
            }

            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;

            try
            {
                // 与 Amethyst 一致：先关掉原版氛围对象。
                SetAmbienceVisible(false);

                // 再关掉原版背景贴图与各层覆盖物 —— 不做这步，自定义图会被它们盖住，
                // 只在半透明面板处透出来。
                HideVanillaLayers();

                if (_art == null)
                {
                    var sprite = Load();
                    if (sprite == null) throw new InvalidDataException("内嵌背景图缺失或解码失败。");

                    _art = new GameObject("AceMainMenuArt");
                    _art.transform.position = Position;
                    var renderer = _art.AddComponent<SpriteRenderer>();
                    renderer.sprite = sprite;
                    FitToScreen(renderer);

                    if (!_logged)
                    {
                        _logged = true;
                        var cam = Camera.main;
                        AntiCheatRuntime.Log?.LogInfo("[主菜单] 已创建背景对象，贴图 " +
                            _texture.width + "x" + _texture.height +
                            "，位置 " + Position.x + "," + Position.y + "," + Position.z +
                            "，相机=" + (cam != null ? cam.name : "无") +
                            "，正交=" + (cam != null && cam.orthographic) +
                            "，正交尺寸=" + (cam != null ? cam.orthographicSize.ToString("F2") : "-") +
                            "，宽高比=" + (cam != null ? cam.aspect.ToString("F3") : "-") +
                            "，缩放=" + _art.transform.localScale.x.ToString("F3"));
                    }
                }
            }
            catch (Exception ex)
            {
                Destroy();
                _failures++;

                if (_failures == 1 || _failures >= MaxFailures)
                    AntiCheatRuntime.Log?.LogWarning("[主菜单] 背景应用失败（第 " + _failures + " 次）：" +
                        ex.GetType().Name + "：" + ex.Message + "\n" + ex.StackTrace);

                if (_failures >= MaxFailures)
                {
                    _gaveUp = true;
                    AntiCheatRuntime.Log?.LogWarning("[主菜单] 连续失败已达上限，停止重试，菜单保持原样。");
                }
            }
        }

        /// <summary>
        /// 按相机视野等比铺满。**与 Amethyst 逐字一致。**
        ///
        /// 正交相机用 `orthographicSize * 2` 作为可视高度、乘宽高比得可视宽度；
        /// 透视相机则按摆放距离与 FOV 算。取宽高比例的较大者（cover），
        /// 保证铺满且不变形。
        ///
        /// 只缩放，**不动位置** —— 位置是写死的 (0,0,600)。
        /// </summary>
        private static void FitToScreen(SpriteRenderer sr)
        {
            try
            {
                var camera = Camera.main;
                if (camera == null || sr == null || sr.sprite == null || _art == null) return;

                var distance = Mathf.Abs(Position.z - camera.transform.position.z);

                float viewHeight;
                float viewWidth;
                if (camera.orthographic)
                {
                    viewHeight = camera.orthographicSize * 2f;
                    viewWidth = viewHeight * camera.aspect;
                }
                else
                {
                    viewHeight = 2f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    viewWidth = viewHeight * camera.aspect;
                }

                var size = sr.sprite.bounds.size;
                if (size.x <= 0f || size.y <= 0f) return;

                var scale = Mathf.Max(viewWidth / size.x, viewHeight / size.y);
                _art.transform.localScale = new Vector3(scale, scale, 1f);
            }
            catch
            {
                // 与 Amethyst 一致：缩放失败就保持原样，不影响菜单。
            }
        }

        /// <summary>
        /// 载入内嵌图并建成 Sprite。**与 Amethyst 一致地设置 hideFlags。**
        ///
        /// `hideFlags |= 0x3D` 里含 DontUnloadUnusedAsset：
        /// 不设的话 Unity 的资源清理会把贴图当垃圾回收，精灵变空，背景随机消失。
        /// 这是我先前漏掉、导致背景时有时无的关键一行。
        /// </summary>
        private static Sprite Load()
        {
            if (_sprite != null) return _sprite;

            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(ResourceNames[SelectedBackground]);
                if (stream == null || stream.Length <= 0 || stream.Length > int.MaxValue) return null;

                using var buffer = new MemoryStream((int)stream.Length);
                stream.CopyTo(buffer);

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(buffer.ToArray()))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                texture.hideFlags = texture.hideFlags | (HideFlags)0x3D;
                _texture = texture;

                _sprite = Sprite.Create(texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), PixelsPerUnit);
                _sprite.hideFlags = _sprite.hideFlags | (HideFlags)0x3D;
                return _sprite;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>隐藏 / 恢复原版氛围对象。与 Amethyst 逐字一致。</summary>
        private static void SetAmbienceVisible(bool visible)
        {
            try
            {
                if (visible)
                {
                    if (_ambienceHidden && _ambience != null) _ambience.SetActive(true);
                    _ambience = null;
                    _ambienceHidden = false;
                    return;
                }

                if (_ambienceHidden && _ambience == null) _ambienceHidden = false;
                if (_ambienceHidden) return;

                var now = Time.realtimeSinceStartup;
                if (now < _nextAmbienceFind) return;
                _nextAmbienceFind = now + 1f;

                _ambience = GameObject.Find(AmbienceName);
                if (_ambience == null) return;

                _ambience.SetActive(false);
                _ambienceHidden = true;
            }
            catch
            {
                // 与 Amethyst 一致：找不到就跳过，不影响菜单。
            }
        }

        /// <summary>
        /// 关掉原版背景贴图与各层覆盖物。**这是背景图能否露出来的关键一步。**
        ///
        /// 逐条对应 Amethyst 的 `AmethystMainMenuOptimize.Tick`：
        ///   HideObject("BackgroundTexture")  ← 游戏真正的背景，不关就永远盖着自定义图
        ///   HideObject("WindowShine") / HideObject("MainUI/Tint") / HideObject("MaskedBlackScreen")
        ///   HideRenderer("LeftPanel") / HideRenderer("RightPanel")
        ///
        /// 用 <c>GameObject.Find</c> 按路径找（纯字符串查找，不触发 IL2CPP 补桩失败）。
        /// 找不到就跳过 —— 不同版本节点名可能不同，不该因此中断。
        /// </summary>
        private static void HideVanillaLayers()
        {
            for (var i = 0; i < HideObjectPaths.Length; i++)
            {
                try
                {
                    var go = GameObject.Find(HideObjectPaths[i]);
                    if (go == null || !go.activeSelf) continue;
                    go.SetActive(false);
                    if (!HiddenObjects.Contains(go)) HiddenObjects.Add(go);
                    AntiCheatRuntime.Log?.LogInfo("[主菜单] 已隐藏原版图层「" + HideObjectPaths[i] + "」。");
                }
                catch { }
            }

            for (var i = 0; i < HideRendererPaths.Length; i++)
            {
                try
                {
                    var go = GameObject.Find(HideRendererPaths[i]);
                    if (go == null) continue;
                    var sr = go.GetComponent<SpriteRenderer>();
                    if (sr == null || !sr.enabled) continue;
                    sr.enabled = false;
                    if (!DisabledRenderers.Contains(sr)) DisabledRenderers.Add(sr);
                    AntiCheatRuntime.Log?.LogInfo("[主菜单] 已关闭原版底板「" + HideRendererPaths[i] + "」。");
                }
                catch { }
            }
        }

        /// <summary>恢复被隐藏 / 关闭的原版图层。离开菜单时调用。</summary>
        private static void RestoreVanillaLayers()
        {
            for (var i = 0; i < HiddenObjects.Count; i++)
            {
                try
                {
                    var go = HiddenObjects[i];
                    if (go != null && !go.activeSelf) go.SetActive(true);
                }
                catch { }
            }
            HiddenObjects.Clear();

            for (var i = 0; i < DisabledRenderers.Count; i++)
            {
                try
                {
                    var sr = DisabledRenderers[i];
                    if (sr != null) sr.enabled = true;
                }
                catch { }
            }
            DisabledRenderers.Clear();
        }

        /// <summary>销毁自建背景并恢复原版氛围对象。</summary>
        private static void Destroy()
        {
            try
            {
                if (_art != null) UnityEngine.Object.Destroy(_art);
            }
            catch { }

            _art = null;
            _nextAmbienceFind = 0f;
            SetAmbienceVisible(true);
            RestoreVanillaLayers();
        }

        public static void Shutdown()
        {
            Destroy();
            if (_sprite != null) UnityEngine.Object.Destroy(_sprite);
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
            _sprite = null;
            _texture = null;
            _menuRoot = null;
            _nextCheck = 0f;
        }
    }
}
