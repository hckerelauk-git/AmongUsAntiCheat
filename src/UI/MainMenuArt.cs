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
    ///      ame 的 `FitToScreen` 仅负责缩放，位置始终为 (0,0,600)。
    ///
    ///   2. **贴图和精灵必须设 hideFlags |= 0x3D。**
    ///      这是我漏掉的一行。0x3D 含 DontUnloadUnusedAsset ——
    ///      不设的话 Unity 的资源清理会把贴图当垃圾回收，精灵变空，背景随机消失。
    ///
    ///   3. **尺寸只用 Camera.main 的正交尺寸与宽高比算。**
    ///      我自作聪明改成「取所有启用相机的较大者 × 安全系数」，
    ///      结果反而遮挡 UI。ame 采用 `orthographicSize * 2` 与 `* aspect`。
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
        /// 该做法成立的前提是相机位于原点附近且朝向 +Z，本机满足。
        /// </summary>
        private static readonly Vector3 Position = new Vector3(0f, 0f, 600f);

        /// <summary>原版氛围对象的名字。与 Amethyst 用的是同一个。</summary>
        private const string AmbienceName = "Ambience";

        /// <summary>
        /// 需要**整体隐藏**的原版对象。**这是让背景图真正露出来的关键。**
        ///
        /// 来自 Amethyst 的 `AmethystMainMenuOptimize.Tick`。此前仅关闭 `Ambience`，
        /// 未意识到还存在 `BackgroundTexture` —— 它才是游戏实际的背景贴图，
        /// 它一直盖在自定义图上，所以图只在半透明面板里透出来。
        ///
        /// **只留这一个。**
        /// Amethyst 还会一并隐藏 `WindowShine` / `MainUI/Tint` / `MaskedBlackScreen`
        /// 并关掉 `LeftPanel` / `RightPanel` 的底板，但那是为了配合它自己重绘的界面。
        /// 我们只要换背景，不需要动这些 —— 实测隐藏它们会导致主菜单「开始」按钮点不动，
        /// 因为其中有些对象和菜单的交互流程绑在一起。
        ///
        /// 改动的原版对象越少，破坏菜单的风险越低。
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
            // 路径从 MainMenuManager 根算起，逐级解析 —— 比 GameObject.Find 可靠
            "MainUI/AspectScaler/RightPanel",                            // 右侧面板底（灰框 ShipWindowFrame）
            "MainUI/AspectScaler/RightPanel/MaskedBlackScreen",          // 右侧带圆角窗口的黑幕 —— 现场那个黑框
            "MainUI/AspectScaler/RightPanel/WindowShine",                // 右侧面板的玻璃反光条
            "MainUI/AspectScaler/LeftPanel",                             // 左侧按钮区的深色底板

            // 右侧那块灰色半透明遮罩。
            // 尺寸 8.7x5.5、颜色 RGBA(0.170, 0.170, 0.170, 0.816)，覆盖右半屏。
            // **子菜单打开时游戏会把它启用**，所以必须靠 ReassertHidden 每帧重申。
            // 定位它花了不少工夫：早期诊断用 `sprite.bounds.size × scale` 算尺寸，
            // 对九宫格精灵会算成 1.0x1.0，正好被「只列大尺寸渲染器」的过滤条件挡掉。
            "MainUI/Tint",

            // 左侧按钮区的分组分隔线（3.2 x 0.0，即一条线）。
            "MainUI/AspectScaler/LeftPanel/Main Buttons/Divider",

            // 右侧子菜单标题下的分隔线（7.6 x 0.0）。
            // 四个子菜单各有一条，全部关掉 —— 只关一条的话，切到另一个子菜单又会冒出来。
            "MainUI/AspectScaler/RightPanel/MaskedBlackScreen/GameModeButtons/Divider",
            "MainUI/AspectScaler/RightPanel/MaskedBlackScreen/OnlineButtons/Divider",
            "MainUI/AspectScaler/RightPanel/MaskedBlackScreen/EnterCodeButtons/Divider",
            "MainUI/AspectScaler/RightPanel/MaskedBlackScreen/AccountButtons/Divider",
        };

        /// <summary>被我们隐藏 / 关掉的原版对象，离开菜单时逐个恢复。</summary>
        private static readonly System.Collections.Generic.List<GameObject> HiddenObjects =
            new System.Collections.Generic.List<GameObject>(8);

        /// <summary>
        /// 需要**变透明**的底板（相对 <c>AccountManager</c> 的路径）。
        ///
        /// 顶栏（好友码那一条）在这里。**做法参考 Amethyst 的 `AmethystFriendsBar`**：
        /// 它走的是 <c>AccountManager.Instance.transform.Find("AccountTab/GameHeader/BarSprite")</c>
        /// 然后 <c>color = Color.clear</c>。
        ///
        /// **为什么改颜色而不是关渲染器**：游戏会重新启用渲染器，
        /// 但透明色一旦设上就一直有效 —— 关渲染器要每帧抢，改颜色只需要维持。
        /// </summary>
        private static readonly string[] TransparentSpritePaths =
        {
            "AccountTab/GameHeader/BarSprite",
        };

        /// <summary>
        /// 顶栏是否已处理过。
        ///
        /// **不能用 ClearedRenderers.Count 当守卫。**
        /// 那个列表同时被「隐藏原版图层」和「顶栏透明化」共用，
        /// 而隐藏图层先执行 —— 列表立刻被填满，守卫直接命中，顶栏永远轮不到。
        /// 这正是 1.1.5 出现过的一次回归：两个用途共用一个列表，彼此把对方短路掉。
        /// </summary>
        private static bool _topBarCleared;

        private static readonly System.Collections.Generic.List<SpriteRenderer> ClearedRenderers =
            new System.Collections.Generic.List<SpriteRenderer>(4);

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
        private static bool _loggedLayers;

        private static MainMenuManager _menu;

        public static void Apply(MainMenuManager menu)
        {
            if (menu == null) return;
            _menu = menu;
            var menuRoot = menu.transform;
            if (_menuRoot != menuRoot)
            {
                Destroy();
                _menuRoot = menuRoot;
                _nextCheck = 0f;
                _failures = 0;
                _gaveUp = false;
                _logged = false;
                _nextAmbienceFind = 0f;
                _topBarCleared = false;
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

            // **每帧重申**：游戏会在打开子菜单时把这些渲染器重新启用
            // （比如 MaskedBlackScreen 就是子菜单的深色底，点「开始」时它会被打开）。
            // 0.5 秒的检查间隔不足以覆盖 —— 表现为深色底闪烁或持续存在。
            // 只做「已是 false 就跳过」的判断，开销可忽略。
            ReassertHidden();
            DiagProbeAt(_menuRoot);

            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;

            try
            {
                // 与 Amethyst 一致：先关掉原版氛围对象。
                SetAmbienceVisible(false);

                // 再关掉原版背景贴图与各层覆盖物 —— 不做这步，自定义图会被它们盖住，
                // 只在半透明面板处透出来。
                HideVanillaLayers();
                ClearTopBar();
                HideMenuButtons();

                if (!_loggedLayers)
                {
                    _loggedLayers = true;
                    LogLargeRenderers(_menuRoot);
                }

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
        /// 此行此前遗漏，是背景时有时无的直接原因。
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
                    // **从菜单根逐级解析**，不用 GameObject.Find。
                    //
                    // GameObject.Find 传入「部分路径」（如 RightPanel/MaskedBlackScreen）
                    // 会直接返回 null，退化按名字查找亦不一定命中 —— 实测即为此情况，
                    // MaskedBlackScreen 一直没被关掉，黑框一直在。
                    // 逐级解析是确定性的，哪一级断了也看得出来。
                    var go = ResolveUnderMenu(HideRendererPaths[i]);
                    if (go == null)
                    {
                        AntiCheatRuntime.Log?.LogInfo("[主菜单] 未找到要关闭的底板「" + HideRendererPaths[i] + "」。");
                        continue;
                    }

                    // **只关这一个物体自己的渲染器，不碰子物体。**
                    //
                    // 曾经改成「递归关整个子树」，结果一次关掉 132 个渲染器 ——
                    // 因为 RightPanel 不是一块面板，而是个大容器，里面装着
                    // MaskedBlackScreen / ScreenCover 这些**菜单切换用的黑幕**。
                    // 关闭黑幕后主菜单与子菜单同时显示，菜单不可用。
                    //
                    // UI 层级中「按名字找到的容器」与「实际需要关闭的贴图」并非同一对象，
                    // 盲目递归会一并关闭无关对象。应精确指定目标。
                    if (NeverDisable.Contains(go.name)) continue;

                    var sr = go.GetComponent<SpriteRenderer>();
                    if (sr == null) continue;

                    // **关渲染器 + 把 alpha 清零，两者都要。**
                    //
                    // 只关 enabled 会有一帧的闪烁：游戏在自己的 Update 里把它重新启用，
                    // 而我们的帧驱动由 HudManager.Update / Canvas 两个入口共同触发、
                    // 按帧去重 —— 谁先跑到就算谁的。HudManager.Update 在游戏逻辑之前，
                    // 于是「我们关掉 → 游戏又打开 → 这一帧照常渲染」。
                    //
                    // alpha 归零之后即使 enabled 被翻回来也画不出东西，
                    // 从「每帧抢」变成「设一次就永久有效」。
                    if (sr.enabled) sr.enabled = false;
                    if (sr.color.a > 0.001f) sr.color = Color.clear;
                    if (!DisabledRenderers.Contains(sr)) DisabledRenderers.Add(sr);
                    if (!ClearedRenderers.Contains(sr)) ClearedRenderers.Add(sr);
                    AntiCheatRuntime.Log?.LogInfo("[主菜单] 已关闭原版底板「" + HideRendererPaths[i] + "」。");
                }
                catch { }
            }
        }

        /// <summary>
        /// **绝对不许关**的渲染器。
        ///
        /// 这些都是菜单切换流程要用的（黑幕、遮罩），关掉会让菜单卡在半途 ——
        /// 表现为「主菜单与开始子菜单同时显示」。
        /// 即使日后有人再写递归关闭，也要先过这一关。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> NeverDisable =
            new System.Collections.Generic.HashSet<string>
            {
                // 注意：MaskedBlackScreen **不在此列** —— 它是右侧的深色底板，
                // 必须关掉。曾经误判成「切换黑幕」放进本名单，导致黑框一直去不掉。
                // 真正由游戏自己按需开关的是 ScreenCover。
                "ScreenCover",
                "FullScreen",
                "ClickBlocker",
            };

        /// <summary>
        /// 一次性诊断：把菜单里「尺寸较大」的 SpriteRenderer 全列出来。
        ///
        /// 找不到待隐藏对象时，该清单可直接指出绘制该框的节点 ——
        /// 比来回猜快得多。只列世界尺寸大于 1 单位的，小图标不用看。
        /// </summary>
        private static void LogLargeRenderers(Transform menuRoot)
        {
            if (menuRoot == null) return;
            try
            {
                var buffer = new System.Text.StringBuilder();
                var found = 0;
                CollectLargeRenderers(menuRoot, buffer, ref found, 0);
                AntiCheatRuntime.Log?.LogInfo("[主菜单] 菜单里的大尺寸渲染器（共 " + found + " 个）：" + buffer);
            }
            catch { }
        }

        private static void CollectLargeRenderers(Transform node, System.Text.StringBuilder buffer,
            ref int found, int depth)
        {
            if (node == null || depth > 6 || found > 80) return;

            try
            {
                var sr = node.GetComponent<SpriteRenderer>();
                if (sr != null && sr.sprite != null)
                {
                    var size = sr.sprite.bounds.size;
                    var scale = node.lossyScale;
                    var w = size.x * Mathf.Abs(scale.x);
                    var h = size.y * Mathf.Abs(scale.y);
                    if (w > 1f && h > 1f)
                    {
                        found++;
                        buffer.Append("\n  ").Append(PathOf(node))
                              .Append("  ").Append(w.ToString("F1")).Append("x").Append(h.ToString("F1"))
                              .Append("  ").Append(sr.sprite.name)
                              .Append("  排序=").Append(sr.sortingOrder)
                              .Append(sr.enabled ? " 启用" : " 已关");
                    }
                }
            }
            catch { }

            var children = 0;
            try { children = node.childCount; } catch { return; }
            for (var i = 0; i < children; i++)
            {
                try { CollectLargeRenderers(node.GetChild(i), buffer, ref found, depth + 1); }
                catch { }
            }
        }

        /// <summary>从根到该节点的路径，便于直接拿去找对象。</summary>
        private static string PathOf(Transform node)
        {
            try
            {
                var path = node.name;
                var parent = node.parent;
                var guard = 0;
                while (parent != null && guard++ < 12)
                {
                    path = parent.name + "/" + path;
                    parent = parent.parent;
                }
                return path;
            }
            catch { return node.name; }
        }

        // ===================================================================
        // 定点探针：右侧那块灰底到底是谁画的？
        //
        // 上一版诊断有两个硬伤，所以没找到真凶：
        //   1. 只扫了 SpriteRenderer，漏掉 UI Image；
        //   2. 用 `sprite.bounds.size × scale` 算尺寸 —— 对九宫格 / 平铺精灵是错的，
        //      真实的绘制范围在 `sr.bounds` 里。
        // 这版直接问「探针点上盖着哪些渲染器」，一次最多 8 行，且只在结果变化时输出。
        // ===================================================================
        /// <summary>探针点：屏幕右侧中部（世界坐标），灰底就在这一带。</summary>
        private static readonly Vector2 ProbePoint = new Vector2(2.0f, 0.0f);

        private static float _nextProbe;
        private static int _probeDumps;
        private static string _lastProbeSignature;

        private static void DiagProbeAt(Transform menuRoot)
        {
            if (menuRoot == null) return;
            if (Time.unscaledTime < _nextProbe) return;
            _nextProbe = Time.unscaledTime + 1.5f;
            if (_probeDumps >= 12) return;

            try
            {
                ProbeHits.Clear();
                CollectAt(menuRoot, ProbeHits, 0);
                if (ProbeHits.Count == 0) return;

                // 从「画在最上面」往下排：
                // 同 sortingOrder 时，正交相机下 z 越大离相机越远、越先画，所以排在后面。
                ProbeHits.Sort((a, b) =>
                {
                    var c = b.sortingOrder.CompareTo(a.sortingOrder);
                    return c != 0 ? c : b.bounds.center.z.CompareTo(a.bounds.center.z);
                });

                var signature = string.Join("|", ProbeHits.ConvertAll(r => PathOf(r.transform)));
                if (signature == _lastProbeSignature) return;   // 没变化就不刷屏
                _lastProbeSignature = signature;
                _probeDumps++;

                var buf = new System.Text.StringBuilder();
                for (var i = 0; i < ProbeHits.Count && i < 8; i++)
                {
                    var r = ProbeHits[i];
                    var b = r.bounds;
                    buf.Append("\n  ").Append(PathOf(r.transform))
                       .Append("  ").Append(b.size.x.ToString("F1")).Append("x").Append(b.size.y.ToString("F1"))
                       .Append("  颜色=").Append(r.color.ToString())
                       .Append("  排序=").Append(r.sortingOrder)
                       .Append("  z=").Append(b.center.z.ToString("F1"))
                       .Append(r.sprite != null ? "  " + r.sprite.name : "  <无贴图>");
                }
                AntiCheatRuntime.Log?.LogInfo("[探针] 点(" + ProbePoint.x + "," + ProbePoint.y + ")上盖着 "
                    + ProbeHits.Count + " 个渲染器（从上到下）：" + buf);
            }
            catch { }
        }

        private static readonly System.Collections.Generic.List<SpriteRenderer> ProbeHits =
            new System.Collections.Generic.List<SpriteRenderer>(16);

        /// <summary>收集「包围盒盖住探针点」的可见渲染器。</summary>
        private static void CollectAt(Transform node, System.Collections.Generic.List<SpriteRenderer> into, int depth)
        {
            if (node == null || depth > 6 || into.Count > 40) return;

            try
            {
                var sr = node.GetComponent<SpriteRenderer>();
                if (sr != null && sr.enabled && node.gameObject.activeInHierarchy)
                {
                    var b = sr.bounds;
                    var probe = new Vector3(ProbePoint.x, ProbePoint.y, b.center.z);
                    if (b.Contains(probe)) into.Add(sr);
                }
            }
            catch { }

            var children = 0;
            try { children = node.childCount; } catch { return; }
            for (var i = 0; i < children; i++)
            {
                try { CollectAt(node.GetChild(i), into, depth + 1); } catch { }
            }
        }

        /// <summary>
        /// 隐藏菜单上不需要的按钮。
        ///
        /// 用游戏自己的字段（<c>MainMenuManager.quitButton</c>）而不是按名字找 ——
        /// 字段是编译期确定的，不会因为节点改名或层级调整而失效。
        /// </summary>
        private static void HideMenuButtons()
        {
            try
            {
                if (_menu?.quitButton == null) return;
                var go = _menu.quitButton.gameObject;
                if (go != null && go.activeSelf && !HiddenObjects.Contains(go))
                {
                    go.SetActive(false);
                    HiddenObjects.Add(go);
                    AntiCheatRuntime.Log?.LogInfo("[主菜单] 已隐藏「退出」按钮。");
                }
            }
            catch { }
        }

        /// <summary>
        /// 一次性诊断：把 MainUI 下的节点连**组件类型**一起打出来。
        ///
        /// 顶栏那类元素不是 SpriteRenderer（所以不在大渲染器清单里），
        /// 只能靠这份层级清单认出来。**不知道层级就先打印，别靠猜** ——
        /// 今天已经因为猜层级栽了两次。
        /// </summary>

        private static void CollectUiTree(Transform node, System.Text.StringBuilder buffer,
            ref int count, int depth)
        {
            // 上限放宽：上一轮 60 条被截断，顶栏很可能就在被截掉的部分里
            // 深度放到 7：顶栏可能在 FullScreen 这类容器下面第 5 层，
            // 上一轮 depth>4 把它截掉了
            if (node == null || depth > 7 || count > 400) return;

            try
            {
                var kinds = new System.Text.StringBuilder();
                try { if (node.GetComponent<UnityEngine.UI.Image>() != null) kinds.Append("Image "); } catch { }
                try { if (node.GetComponent<UnityEngine.UI.Button>() != null) kinds.Append("Button "); } catch { }
                try { if (node.GetComponent<PassiveButton>() != null) kinds.Append("PassiveButton "); } catch { }
                try { if (node.GetComponent<TMPro.TextMeshPro>() != null) kinds.Append("TMP "); } catch { }
                try { if (node.GetComponent<Canvas>() != null) kinds.Append("Canvas "); } catch { }
                try { if (node.GetComponent<SpriteRenderer>() != null) kinds.Append("Sprite "); } catch { }

                if (kinds.Length > 0)
                {
                    count++;
                    buffer.Append("\n  ").Append(PathOf(node)).Append("  [").Append(kinds.ToString().Trim()).Append(']');
                }
            }
            catch { }

            var children = 0;
            try { children = node.childCount; } catch { return; }
            for (var i = 0; i < children; i++)
            {
                try { CollectUiTree(node.GetChild(i), buffer, ref count, depth + 1); }
                catch { }
            }
        }

        /// <summary>
        /// 把顶栏（好友码那一条）的底板变透明。
        ///
        /// 对象在 <c>AccountManager</c> 下，不在 `MainMenuManager` 里 ——
        /// 所以之前扫了整个菜单树都找不到它。
        /// </summary>
        private static void ClearTopBar()
        {
            if (_topBarCleared) return;

            try
            {
                var account = UnityEngine.Object.FindObjectOfType<AccountManager>();
                if (account == null)
                {
                    AntiCheatRuntime.Log?.LogWarning("[主菜单] 找不到 AccountManager，顶栏透明化跳过。");
                    return;
                }


                var root = account.transform;
                for (var i = 0; i < TransparentSpritePaths.Length; i++)
                {
                    var node = root.Find(TransparentSpritePaths[i]);
                    if (node == null) continue;

                    var sr = node.GetComponent<SpriteRenderer>();
                    if (sr == null) continue;
                    if (!ClearedRenderers.Contains(sr)) ClearedRenderers.Add(sr);

                    sr.color = Color.clear;
                    _topBarCleared = true;
                    AntiCheatRuntime.Log?.LogInfo("[主菜单] 已把顶栏底板变透明：" + TransparentSpritePaths[i]);
                }
            }
            catch { }
        }

        /// <summary>
        /// 每帧重申「这些渲染器必须是关的」。
        ///
        /// 游戏自己的逻辑会重新打开它们 —— `MaskedBlackScreen` 既是右侧那块深色底，
        /// 又是整个子菜单（在线 / 账号 / 输入码）的父容器，点「开始」时游戏会启用它。
        /// 停用整个物体不行（子菜单会跟着没），只能盯住渲染器反复关。
        /// </summary>
        private static bool _loggedReassert = false;

        private static void ReassertHidden()
        {
            if (DisabledRenderers.Count == 0) return;
            
            if (!_loggedReassert)
            {
                AntiCheatRuntime.Log?.LogInfo($"[主菜单] ReassertHidden 首次执行 —— DisabledRenderers={DisabledRenderers.Count}");
                _loggedReassert = true;
            }

            for (var i = 0; i < DisabledRenderers.Count; i++)
            {
                try
                {
                    var sr = DisabledRenderers[i];
                    if (sr != null && sr.enabled) sr.enabled = false;
                }
                catch { }
            }

            // 透明色同样要重申 —— 游戏重绘时会把它刷回不透明
            for (var i = 0; i < ClearedRenderers.Count; i++)
            {
                try
                {
                    var sr = ClearedRenderers[i];
                    if (sr != null && sr.color.a > 0.001f) sr.color = Color.clear;
                }
                catch { }
            }
        }

        /// <summary>
        /// 从菜单根按「斜杠分隔的路径」逐级查找子物体。
        ///
        /// 比 <c>GameObject.Find</c> 可靠：后者传部分路径直接返回 null，
        /// 而且只搜激活对象、同名还可能歧义。逐级走是确定性的。
        /// </summary>
        private static GameObject ResolveUnderMenu(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var node = _menuRoot;
            if (node == null) return null;

            var parts = path.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;

                Transform next = null;
                var count = 0;
                try { count = node.childCount; } catch { return null; }

                for (var c = 0; c < count; c++)
                {
                    try
                    {
                        var child = node.GetChild(c);
                        if (child != null && child.name == parts[i]) { next = child; break; }
                    }
                    catch { }
                }

                if (next == null) return null;
                node = next;
            }

            try { return node.gameObject; } catch { return null; }
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
