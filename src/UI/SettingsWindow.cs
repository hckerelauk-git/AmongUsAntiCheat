using System;
using System.Collections.Generic;
using ApexCheatEnder.Config;
using ApexCheatEnder.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ApexCheatEnder.UI
{
    /// <summary>
    /// 设置界面：标题栏拖动，内容区域滚动，改动实时保存。
    ///
    /// 交互不使用 uGUI 的 Button/Toggle：它们的 onClick 要往 il2cpp
    /// 事件上挂托管委托，在动态注册的类型上不稳。这里每帧读鼠标位置，
    /// 用 RectTransformUtility 做命中测试，自己分发点击。
    /// </summary>
    internal static class SettingsWindow
    {
        // 窗口尺寸改为可调：拖右下角的小方块改，松手写回配置，下次启动沿用。
        // 默认 1000x700（800x560 → 1000x700 是因为用户反馈字太小）。
        private const float DefaultWindowWidth = 960f;
        private const float DefaultWindowHeight = 620f;
        private static float WindowWidth = DefaultWindowWidth;
        private static float WindowHeight = DefaultWindowHeight;

        /// <summary>拖拽缩放时的下限与上限。太小放不下内容，太大没意义。</summary>
        private const float MinWindowWidth = 640f;
        private const float MaxWindowWidth = 1920f;
        private const float MinWindowHeight = 460f;
        private const float MaxWindowHeight = 1200f;

        /// <summary>右下角缩放手柄的边长。</summary>
        private const float ResizeGripSize = 22f;
        private const float TitleBarHeight = 72f;
        private const float TabColumnWidth = 166f;
        private const float FooterHeight = 54f;
        private const float ContentPadding = 34f;

        // ================= 开关控件尺寸 =================
        // 轨道 52×26，滑块直径 20，左右各留 3px 内边距。
        // 滑块的 x 用它中心到轨道左边缘的距离表示（anchoredPosition.x）。
        private const float SwitchKnobOffX = 13f;   // 3 + 20/2
        private const float SwitchKnobOnX = 39f;    // 52 - 3 - 20/2

        // ================= 侧边栏尺寸 =================
        // 页签 44 高、间隔 52：留 8px 空隙，比原来 42/48 更透气。
        private const float TabHeight = 50f;
        private const float TabSpacing = 62f;

        /// <summary>窗口圆角半径（与监控面板同一套 9 宫格卡片）。</summary>
        private const int WindowRadius = 16;

        /// <summary>内容区底部留白，避免最后一行贴住 footer。</summary>
        private const float ContentBottomPadding = 12f;

        // ================= 状态 =================

        private static bool _built;
        private static bool _visible;
        private static GameObject _root;
        private static RectTransform _tabArea;
        private static RectTransform _contentArea;

        /// <summary>
        /// 视口：带 RectMask2D 的裁剪容器。
        ///
        /// 为什么必须有它：设置项最多的那一页有 14 行，每行 58px 需要 870px，
        /// 而内容区只有约 574px。没有裁剪 + 滚动的话，**最后 5~6 个选项会被渲染到
        /// 窗口外，用户既看不见也点不到**（这是之前真实存在的问题）。
        /// </summary>
        private static RectTransform _viewport;
        private static float _scrollOffset;
        private static float _contentHeight;
        private static float _viewportHeight;

        /// <summary>滚动惯性的速度缓存（像素/秒），松手后衰减。</summary>
        private static float _scrollVelocity;

        /// <summary>上一帧鼠标 Y，用于计算拖拽增量。</summary>
        private static float _lastMouseY;

        /// <summary>行值刷新的下次时刻。原先每帧刷，纯属浪费。</summary>
        private static float _nextRowRefresh;

        /// <summary>按下时的鼠标位置，用于区分「点击」与「拖拽滚动」。</summary>
        private static Vector3 _pressPos;
        private static bool _pressArmed;

        private static Text _pageTitleText;
        private static Text _pageHintText;
        private static Text _footerText;
        private static Text _hintText;
        private static int _currentPage;

        private static readonly List<TabEntry> Tabs = new List<TabEntry>();
        private static readonly List<RowEntry> Rows = new List<RowEntry>();

        /// <summary>分组小标题的节点，换页时统一销毁。</summary>
        private static readonly List<GameObject> Captions = new List<GameObject>();
        private static readonly List<TabEntry> SubTabs = new List<TabEntry>();
        private static readonly int[] SelectedSubTabs = new int[6];
        private static readonly DoubleClickConfirmation RestoreConfirmation = new DoubleClickConfirmation();
        private static string _exportStatus = "点击导出";
        private static string _selectedDetail;
        private static int _historyPage;
        private static int _historyGroup;
        private static int _preset;
        private static RectTransform _subTabArea;
        private static RowEntry _activeSlider;
        private static RectTransform _windowRect;
        private static RectTransform _titleRect;
        private static RectTransform _resizeGrip;
        private static Transform _canvasRoot;
        private static bool _windowResizing;
        private static Vector2 _resizeStartMouse;
        private static float _resizeStartWidth;
        private static float _resizeStartHeight;
        private static float _nextLiveRelayout;
        private static bool _windowDragging;
        private static bool _scrollDragging;
        private static bool _gestureMoved;
        private static Vector2 _dragStart;
        private static Vector2 _windowStart;

        /// <summary>底部「已保存」提示的显示截止时刻。</summary>
        private static float _savedFlashUntil;

        private sealed class TabEntry
        {
            public int Index;
            public RectTransform Rect;
            public Image Background;
            public Image Marker;
            public Image Glyph;
            public Text Label;
        }

        private sealed class RowEntry
        {
            public SettingRow Model;
            public RectTransform Rect;
            public Image Background;
            public Text Value;
            public RectTransform MinusRect;
            public RectTransform PlusRect;
            public RectTransform SliderRect;
            public RectTransform SliderFill;
            public RectTransform SliderKnob;

            /// <summary>开关行的轨道底板（仅 Toggle 行有）。</summary>
            public Image SwitchTrack;

            /// <summary>开关行的滑块（仅 Toggle 行有）。移动它来表达开/关。</summary>
            public RectTransform SwitchKnob;

            /// <summary>
            /// 这一行的说明文字。
            ///
            /// 行内不显示，鼠标悬停时由底部描述栏显示 —— 这是把界面从
            /// 「拥挤臃肿」压下去的关键：行高因此从 100 降到 46。
            /// </summary>
            public string Hint;
        }

        // ================= 行模型 =================

        private enum RowKind { Toggle, Number, Info, Action }

        private sealed class SettingRow
        {
            public string Label;
            public string Hint;
            public RowKind Kind;
            public Func<string> GetValue;
            public float Min;
            public float Max;
            public float Step;
            public bool Integer;
            public Func<float> ReadNumber;
            public Action<float> WriteNumber;

            /// <summary>
            /// 开关行的当前状态（仅 Toggle 行提供）。
            /// 之前靠解析 GetValue() 返回的 "[ 开 ]" 字符串来判断颜色与位置 ——
            /// 依靠中文字符推断状态，文案变更即失效。改由此处直接提供布尔值。
            /// </summary>
            public Func<bool> GetState;

            public Action OnToggle;
            public Action OnMinus;
            public Action OnPlus;
        }

        private static readonly string[] TabNames =
        {
            "常规",
            "行为检测",
            "处置规则",
            "网络防护",
            "关于",
            "记录与工具",
        };

        /// <summary>
        /// 每个页签的图标形状，与 <see cref="TabNames"/> 一一对应。
        /// 使用形状而非内置图片：插件需保证「单个 DLL 即完整插件」，
        /// 且这些几何图形由 <c>AceTheme.Glyph</c> 程序生成，任意尺寸都清晰。
        /// </summary>
        private static readonly AceTheme.GlyphShape[] TabGlyphs =
        {
            AceTheme.GlyphShape.Square,     // 开着什么：开关方块
            AceTheme.GlyphShape.Triangle,   // 跑多快算作弊：速度
            AceTheme.GlyphShape.Diamond,    // 抓到怎么办：处置
            AceTheme.GlyphShape.Circle,     // 爬管道：管道口
            AceTheme.GlyphShape.Bars,       // 关于：条目
            AceTheme.GlyphShape.Bars,
        };

        private static readonly string[] TabHints =
        {
            "检测模块开关与界面选项。",
            "按移动、动作、会议分类调整检测阈值。",
            "处置策略与规则记录方式。",
            "通风管、滑索与网络层防护。",
            "版本信息与快捷键。",
            "本地事件、风险详情、阈值预设与脱敏诊断。数据不上传。",
        };

        // ================= 构建 =================

        public static void EnsureBuilt(Transform canvasRoot)
        {
            if (_built || canvasRoot == null) return;
            _built = true;

            _canvasRoot = canvasRoot;

            // 尺寸优先取用户上次拖出来的值，取不到才用默认。
            var cfg = AntiCheatRuntime.Config;
            WindowWidth = Mathf.Clamp(cfg?.SettingsWindowWidth.Value ?? (int)DefaultWindowWidth,
                MinWindowWidth, MaxWindowWidth);
            WindowHeight = Mathf.Clamp(cfg?.SettingsWindowHeight.Value ?? (int)DefaultWindowHeight,
                MinWindowHeight, MaxWindowHeight);

            var font = UiBuilder.LoadFont(15);

            _root = UiBuilder.CreateNode("AceSettings", canvasRoot);
            var rootRect = _root.GetComponent<RectTransform>();
            _windowRect = rootRect;
            var titleNode = UiBuilder.CreateNode("DragTitleBar", _root.transform);
            _titleRect = titleNode.GetComponent<RectTransform>();
            UiBuilder.Place(_titleRect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                Vector2.zero, new Vector2(WindowWidth, TitleBarHeight));
            UiBuilder.Place(rootRect,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(WindowWidth, WindowHeight));

            // ---- 窗口底板：圆角卡片（与监控面板同一套视觉） ----
            var bg = UiBuilder.CreateImage("Bg", _root.transform, Color.white);
            bg.sprite = AceTheme.Card(WindowRadius, 1, AceTheme.WindowBg, AceTheme.Border);
            bg.type = Image.Type.Sliced;
            UiBuilder.Stretch(bg.rectTransform, 0f, 0f, 0f, 0f);

            // ---- 标题区 ----
            // 装饰件全部拆掉：横贯窗口的顶部强调条、盾牌图标、四处分隔线、状态条。
            // 各自尺寸不大，叠加后即形成视觉拥挤。
            // 现在只留图标 + 标题 + 一行副标题 + 右侧快捷键提示，靠留白撑开。
            var iconTex = AceTheme.Icon();
            if (iconTex != null)
            {
                var icon = UiBuilder.CreateImage("Icon", _root.transform, Color.white, iconTex);
                icon.raycastTarget = false;
                UiBuilder.Place(icon.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(18f, -16f), new Vector2(32f, 32f));
            }

            // 有图标时标题右移，没有就贴左 —— 图标缺失也不该让标题错位到中间。
            var titleLeft = iconTex != null ? 60f : 18f;

            var title = CreateText("Title", _root.transform,
                "Apex Cheat Ender", font, 17, AceTheme.TextMain, TextAnchor.UpperLeft, FontStyle.Bold);
            UiBuilder.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(titleLeft, -16f), new Vector2(360f, 24f));

            var subtitle = CreateText("SubTitle", _root.transform,
                "设置", font, 12, AceTheme.TextDim);
            UiBuilder.Place(subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(titleLeft, -38f), new Vector2(360f, 18f));

            var hint = CreateText("Hint", _root.transform,
                "Insert 关闭", font, 12, AceTheme.TextDim, TextAnchor.UpperRight);
            UiBuilder.Place(hint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(WindowWidth - 180f, -18f), new Vector2(160f, 20f));

            var divTop = UiBuilder.CreateImage("DivTop", _root.transform, AceTheme.Border);
            UiBuilder.Place(divTop.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, -TitleBarHeight), new Vector2(WindowWidth, 1f));

            // ---- 左侧页签栏 ----
            // 导航必须是窗口直属层，不能挂在 viewport/content 的父级上。
            // 旧层级会让页签与滚动裁剪层发生层级竞争，表现为截图里的「空侧栏」。
            var tabAreaNode = UiBuilder.CreateNode("TabArea", _root.transform);
            _tabArea = tabAreaNode.GetComponent<RectTransform>();
            UiBuilder.Place(_tabArea, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0f, (FooterHeight - TitleBarHeight) / 2f),
                new Vector2(TabColumnWidth, WindowHeight - TitleBarHeight - FooterHeight));

            // 页签不再套一层底色方框，也不画左侧分隔线：
            // 选中态使用文字色与竖色条表示，未选中为普通灰字。

            // ---- 页标题与页说明：固定在窗口上，不随内容滚动 ----
            var textLeft = TabColumnWidth + ContentPadding;
            _pageTitleText = CreateText("PageTitle", _root.transform,
                "", font, 17, AceTheme.Accent, TextAnchor.UpperLeft, FontStyle.Bold);
            UiBuilder.Place(_pageTitleText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(textLeft, -TitleBarHeight - 10f), new Vector2(500f, 22f));

            _pageHintText = CreateText("PageHint", _root.transform,
                "", font, 13, AceTheme.TextDim, TextAnchor.UpperLeft);
            UiBuilder.Place(_pageHintText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(textLeft, -TitleBarHeight - 32f), new Vector2(WindowWidth - textLeft - ContentPadding, 24f));

            // ---- 滚动视口：裁剪 + 承载行内容 ----
            // 设置项最多的一页有 14 行（需 870px），而可用高度只有约 520px。
            // 没有这层裁剪，最后 5~6 个选项会渲染到窗口外，用户看不见也点不到。
            var viewportNode = UiBuilder.CreateNode("Viewport", _root.transform);
            _viewport = viewportNode.GetComponent<RectTransform>();
            var vpW = WindowWidth - TabColumnWidth - ContentPadding * 2f;
            var subNode = UiBuilder.CreateNode("SubTabs", _root.transform);
            _subTabArea = subNode.GetComponent<RectTransform>();
            UiBuilder.Place(_subTabArea, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(textLeft, -TitleBarHeight - 60f), new Vector2(vpW, 30f));
            var vpH = WindowHeight - TitleBarHeight - 96f - FooterHeight - ContentBottomPadding;
            UiBuilder.Place(_viewport, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(textLeft, -TitleBarHeight - 96f), new Vector2(vpW, vpH));
            _viewportHeight = vpH;
            viewportNode.AddComponent<RectMask2D>();

            var content = UiBuilder.CreateNode("Content", viewportNode.transform);
            _contentArea = content.GetComponent<RectTransform>();
            // 内容锚在视口顶部，高度由 BuildRows 按行数设置，靠 anchoredPosition.y 上下移动
            _contentArea.anchorMin = new Vector2(0f, 1f);
            _contentArea.anchorMax = new Vector2(1f, 1f);
            _contentArea.pivot = new Vector2(0.5f, 1f);
            _contentArea.anchoredPosition = Vector2.zero;
            _contentArea.sizeDelta = new Vector2(0f, vpH);

            var divBottom = UiBuilder.CreateImage("DivBottom", _root.transform, AceTheme.Border);
            UiBuilder.Place(divBottom.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(0f, FooterHeight), new Vector2(WindowWidth, 1f));

            // ---- 底部描述栏 ----
            // 行的说明文字全部搬到这里，鼠标悬停哪一行就显示哪一行的说明。
            // 这是把界面从「拥挤臃肿」压下去的关键：行内只剩名称 + 控件，
            // 行高从 100 降到 46，一屏能看到的设置项翻了一倍多。
            _hintText = CreateText("HintBar", _root.transform,
                "", font, 12, AceTheme.TextMain);
            UiBuilder.Place(_hintText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(18f, 28f), new Vector2(WindowWidth - 36f, 20f));
            _hintText.verticalOverflow = VerticalWrapMode.Truncate;

            _footerText = CreateText("Footer", _root.transform,
                "", font, 11, AceTheme.TextDim);
            UiBuilder.Place(_footerText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(18f, 9f), new Vector2(WindowWidth - 36f, 18f));

            // 导航置于固定背景之后，且完全脱离 RectMask2D。
            tabAreaNode.transform.SetAsLastSibling();

            // ---- 右下角缩放手柄 ----
            // 三个沿对角线递减的小方块，是通用的「可拖拽缩放」视觉语言。
            var gripNode = UiBuilder.CreateNode("ResizeGrip", _root.transform);
            _resizeGrip = gripNode.GetComponent<RectTransform>();
            UiBuilder.Place(_resizeGrip, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-4f, 4f), new Vector2(ResizeGripSize, ResizeGripSize));
            for (var i = 0; i < 3; i++)
            {
                var dot = UiBuilder.CreateImage("GripDot" + i, gripNode.transform, AceTheme.TextDim);
                dot.raycastTarget = false;
                var size = 4f - i;
                UiBuilder.Place(dot.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                    new Vector2(-2f - i * 6f, 2f + i * 6f), new Vector2(size + 2f, size + 2f));
            }

            BuildTabs(font);
            SwitchPage(0);

            _root.SetActive(false);
        }

        private static void BuildTabs(Font font)
        {
            Tabs.Clear();

            for (var i = 0; i < TabNames.Length; i++)
            {
                var index = i;
                var node = UiBuilder.CreateNode("Tab" + i, _tabArea);
                var rect = node.GetComponent<RectTransform>();
                UiBuilder.Place(rect, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(14f, -20f - i * TabSpacing),
                    new Vector2(TabColumnWidth - 28f, TabHeight));

                var bg = node.AddComponent<Image>();
                bg.sprite = AceTheme.Card(9, 0, Color.white, Color.white);
                bg.type = Image.Type.Sliced;
                bg.color = new Color(0f, 0f, 0f, 0f);   // 未选中时全透明
                bg.raycastTarget = false;

                // 选中标记：贴左侧的圆角短竖条。
                // 原来是 4×42 顶满行高的实心长条，太粗太满；改成 3×26 并留出上下留白。
                var marker = UiBuilder.CreateImage("Marker" + i, node.transform, AceTheme.Accent);
                marker.sprite = AceTheme.Card(2, 0, Color.white, Color.white);
                marker.type = Image.Type.Sliced;
                UiBuilder.Place(marker.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    Vector2.zero, new Vector2(3f, 26f));

                // 图标：白色贴图 + Image.color 上色。
                // 贴图若带颜色，Image.color 会与之相乘，切换选中态时会变色失真。
                var glyph = UiBuilder.CreateImage("Glyph" + i, node.transform,
                    AceTheme.TextDim, AceTheme.Glyph(TabGlyphs[i], 18, Color.white));
                UiBuilder.Place(glyph.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(16f, 0f), new Vector2(18f, 18f));

                var label = CreateText("TabLabel" + i, node.transform,
                    TabNames[i], font, 14, AceTheme.TextDim, TextAnchor.MiddleLeft);
                UiBuilder.Place(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(42f, 0f), new Vector2(TabColumnWidth - 50f, 26f));
                label.horizontalOverflow = HorizontalWrapMode.Overflow;

                Tabs.Add(new TabEntry
                {
                    Index = index,
                    Rect = rect,
                    Background = bg,
                    Marker = marker,
                    Glyph = glyph,
                    Label = label,
                });
            }
        }

        // ================= 页面切换 =================

        private static void SwitchPage(int index)
        {
            RestoreConfirmation.Cancel();
            _currentPage = index;
            _pageTitleText.text = TabNames[index];
            if (_pageHintText != null) _pageHintText.text = TabHints[index];

            for (var i = 0; i < Tabs.Count; i++)
            {
                var selected = i == index;

                Tabs[i].Label.color = selected ? AceTheme.TextMain : AceTheme.TextDim;
                // 选中态加粗：只靠颜色区分在深色底上不够明显
                Tabs[i].Label.fontStyle = selected ? FontStyle.Bold : FontStyle.Normal;

                Tabs[i].Background.color = selected
                    ? AceTheme.TabActiveBg
                    : new Color(0f, 0f, 0f, 0f);

                if (Tabs[i].Marker != null)
                    Tabs[i].Marker.color = selected ? AceTheme.Accent : new Color(0f, 0f, 0f, 0f);

                if (Tabs[i].Glyph != null)
                    Tabs[i].Glyph.color = selected ? AceTheme.Primary : AceTheme.TextDim;
            }

            BuildSubTabs(index);
            BuildRows(index);
        }

        private static void BuildSubTabs(int page)
        {
            foreach (var tab in SubTabs)
            {
                tab.Rect.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(tab.Rect.gameObject);
            }
            SubTabs.Clear();
            // 子标签由 SettingsLayout 的表驱动，每页各有自己的划分。
            // 只有一个子标签的页面直接不画 —— 画了也没得切，纯占地方。
            var defs = SettingsLayout.SubTabsFor(page);
            if (defs.Length <= 1) return;
            if (SelectedSubTabs[page] >= defs.Length) SelectedSubTabs[page] = 0;

            var width = (WindowWidth - TabColumnWidth - ContentPadding * 2f - (defs.Length - 1) * 6f) / defs.Length;
            for (var i = 0; i < defs.Length; i++)
            {
                var image = UiBuilder.CreateImage("SubTab" + i, _subTabArea, i == SelectedSubTabs[page] ? AceTheme.TabActiveBg : AceTheme.RowBgA);
                UiBuilder.Place(image.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(i * (width + 6f), 0f), new Vector2(width, 30f));
                var text = CreateText("Label", image.transform, defs[i].Name, UiBuilder.LoadFont(14), 13,
                    i == SelectedSubTabs[page] ? AceTheme.Accent : AceTheme.TextDim, TextAnchor.MiddleCenter);
                UiBuilder.Stretch(text.rectTransform, 4f, 4f, 0f, 0f);
                SubTabs.Add(new TabEntry { Index = i, Rect = image.rectTransform });
            }
        }

        // ================= 行构建 =================

        private static void BuildRows(int page)
        {
            _activeSlider = null;
            _pressArmed = _scrollDragging = false;

            // ── 清空内容区 ──
            // 行和分组标题现在都直接挂在 _contentArea 下面。
            // 原来只销毁分组标题、不销毁行节点，于是换页或改配置触发重建时，
            // 旧行会残留在原位与新行重叠 —— 表现为文字重影、内容糊成一团。
            // 必须整个清空，重建才是干净的。
            if (_contentArea != null)
            {
                for (var c = _contentArea.childCount - 1; c >= 0; c--)
                {
                    var child = _contentArea.GetChild(c);
                    if (child == null) continue;
                    child.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }

            Captions.Clear();
            Rows.Clear();

            // 换页回到顶部：否则会停留在上一页的滚动位置，看起来像"内容没了"
            _scrollOffset = 0f;
            _scrollVelocity = 0f;

            var model = BuildPageModel(page);
            var font = UiBuilder.LoadFont(15);
            var contentWidth = WindowWidth - TabColumnWidth - ContentPadding * 2f;

            // ── 扁平列表 ──
            // 不再用「卡片 + 可折叠分组」：那是两级导航，用户要展开才知道里面有什么，
            // 且每张卡片均有独立圆角底板与边框，视觉上过于零碎。
            // 现改为：分组仅作为一行小号标题，内容紧随其后，一屏可览。
            var cursor = 0f;
            var group = -1;
            for (var i = 0; i < model.Count; i++)
            {
                var row = model[i];
                var rowGroup = SettingsLayout.Group(page, i);
                if (!SettingsLayout.RowVisible(page, i, SelectedSubTabs[page])) continue;

                if (group != rowGroup)
                {
                    group = rowGroup;
                    var caption = CreateText("Section" + group, _contentArea,
                        page < SettingsLayout.GroupNames.Length && group < SettingsLayout.GroupNames[page].Length
                            ? SettingsLayout.GroupNames[page][group]
                            : "其他",
                        font, 12, AceTheme.TextDim, TextAnchor.LowerLeft);
                    UiBuilder.Place(caption.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                        new Vector2(2f, -cursor), new Vector2(contentWidth, SettingsLayout.SectionCaptionHeight));
                    caption.supportRichText = false;
                    Captions.Add(caption.gameObject);
                    cursor += SettingsLayout.SectionCaptionHeight;
                }

                var hasHint = !string.IsNullOrEmpty(row.Hint);
                var rowHeight = SettingsLayout.RowHeight(hasHint);
                var y = -cursor;
                cursor += rowHeight;

                var node = UiBuilder.CreateNode("Row" + i, _contentArea);
                var rect = node.GetComponent<RectTransform>();
                UiBuilder.Place(rect, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(0f, y), new Vector2(contentWidth, rowHeight));

                // 行底不再是一块圆角卡片，只用一条极细分隔线。
                // 描边颜色压到很低的不透明度，靠留白分隔而不是靠框线。
                var divider = UiBuilder.CreateImage("Divider", node.transform, AceTheme.Border);
                divider.raycastTarget = false;
                UiBuilder.Place(divider.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                    new Vector2(0f, 0f), new Vector2(contentWidth, 1f));

                // 名称 + 说明：说明**常驻在行内**，不再要求悬停。
                //
                // 之前为了压高度把说明挪到了底部描述栏、只在悬停时显示，
                // 结果是「不把鼠标挨个划一遍就不知道每项是干什么的」——
                // 以少量高度换取不可读的列表，取舍不成立。
                var label = CreateText("RowLabel" + i, node.transform,
                    row.Label, font, 14, AceTheme.TextMain,
                    hasHint ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft);
                UiBuilder.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(2f, hasHint ? -6f : -rowHeight * 0.5f),
                    new Vector2(contentWidth - 140f, hasHint ? 20f : rowHeight));
                label.horizontalOverflow = HorizontalWrapMode.Overflow;
                label.verticalOverflow = VerticalWrapMode.Truncate;
                label.supportRichText = false;

                if (hasHint)
                {
                    var desc = CreateText("RowDesc" + i, node.transform,
                        row.Hint, UiBuilder.LoadFont(11), 11, AceTheme.TextDim, TextAnchor.UpperLeft);
                    UiBuilder.Place(desc.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                        new Vector2(2f, -26f), new Vector2(contentWidth - 140f, 34f));
                    desc.horizontalOverflow = HorizontalWrapMode.Wrap;
                    desc.verticalOverflow = VerticalWrapMode.Truncate;
                    desc.supportRichText = false;
                }

                var hintText = row.Hint ?? string.Empty;

                if (row.Kind == RowKind.Number)
                {
                    var value = CreateText("RowValue" + i, node.transform,
                        "", font, 14, AceTheme.Accent, TextAnchor.MiddleRight, FontStyle.Bold);
                    UiBuilder.Place(value.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                        new Vector2(-2f, 0f), new Vector2(70f, rowHeight));
                    var inset = row.Integer ? 46f : 18f;
                    var slider = UiBuilder.CreateNode("SliderHit", node.transform).GetComponent<RectTransform>();
                    UiBuilder.Place(slider, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                        new Vector2(-78f, 0f), new Vector2(200f, 28f));
                    var track = UiBuilder.CreateImage("Track", slider, AceTheme.SwitchOff);
                    UiBuilder.Place(track.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                        new Vector2(inset, 0f), new Vector2(200f - inset * 2f, 4f));
                    var fill = UiBuilder.CreateImage("Fill", slider, AceTheme.Accent);
                    UiBuilder.Place(fill.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                        new Vector2(inset, 0f), new Vector2(0f, 4f));
                    var knob = UiBuilder.CreateImage("SliderKnob", slider, AceTheme.SwitchKnob);
                    knob.sprite = AceTheme.Dot(12, Color.white);
                    UiBuilder.Place(knob.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
                        new Vector2(inset, 0f), new Vector2(12f, 12f));
                    Rows.Add(new RowEntry
                    {
                        Model = row, Rect = rect, Background = null, Value = value,
                        SliderRect = slider, SliderFill = fill.rectTransform, SliderKnob = knob.rectTransform,
                        Hint = hintText,
                        MinusRect = row.Integer ? BuildArrow(node.transform, font, 0f, "<", AceTheme.BtnMinusBg) : null,
                        PlusRect = row.Integer ? BuildArrow(node.transform, font, 30f, ">", AceTheme.BtnPlusBg) : null,
                    });
                    continue;
                }

                // 开关行：画一个真正的开关（轨道 + 滑块），不再用 "[ 开 ]" 这种
                // 方括号文本 —— 那是调试输出的样子，不是成品 UI。
                if (row.Kind == RowKind.Toggle && row.GetState != null)
                {
                    var swNode = UiBuilder.CreateNode("Switch", node.transform);
                    var swRect = swNode.GetComponent<RectTransform>();
                    UiBuilder.Place(swRect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                        new Vector2(-2f, 0f), new Vector2(46f, 24f));

                    // 贴图一律用白色，状态色交给 Image.color。
                    // 若把底色烤进贴图，Image.color 会与之相乘：
                    // SwitchOff × SwitchOn ≈ 黑，开关就废了。
                    var track = swNode.AddComponent<Image>();
                    track.sprite = AceTheme.Card(12, 0, Color.white, Color.white);
                    track.type = Image.Type.Sliced;
                    track.raycastTarget = false;

                    var knobNode = UiBuilder.CreateNode("Knob", swNode.transform);
                    var knobRect = knobNode.GetComponent<RectTransform>();
                    UiBuilder.Place(knobRect, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
                        new Vector2(SwitchKnobOffX, 0f), new Vector2(20f, 20f));
                    var knob = knobNode.AddComponent<Image>();
                    knob.sprite = AceTheme.Dot(20, Color.white);
                    knob.color = AceTheme.SwitchKnob;
                    knob.raycastTarget = false;

                    Rows.Add(new RowEntry
                    {
                        Model = row,
                        Rect = rect,
                        Background = null,
                        SwitchTrack = track,
                        SwitchKnob = knobRect,
                        Hint = hintText,
                    });
                    continue;
                }

                var normalValue = CreateText("RowValue" + i, node.transform,
                    "", font, 14, AceTheme.Accent, TextAnchor.MiddleRight, FontStyle.Bold);
                UiBuilder.Place(normalValue.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                    new Vector2(-2f, 0f), new Vector2(120f, rowHeight));
                Rows.Add(new RowEntry
                {
                    Model = row,
                    Rect = rect,
                    Background = null,
                    Value = normalValue,
                    Hint = hintText,
                });
            }

            _contentHeight = cursor + ContentBottomPadding;
            _contentArea.sizeDelta = new Vector2(0f, Mathf.Max(_contentHeight, _viewportHeight));
            _contentArea.anchoredPosition = Vector2.zero;
            RefreshRowValues();
        }

        /// <summary>
        /// 悬停哪一行，就在底部描述栏显示哪一行的说明。
        ///
        /// 说明文字不再逐行常驻显示，这是把行高压到 46 的前提。
        /// 找不到悬停行时保留上一次的说明 —— 鼠标在行间移动时文字不会闪。
        /// </summary>
        private static void UpdateHoverHint(Vector2 mouse)
        {
            if (_hintText == null) return;

            foreach (var row in Rows)
            {
                if (row.Rect == null) continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(row.Rect, mouse, null)) continue;

                var hint = row.Hint;
                if (string.IsNullOrEmpty(hint)) return;

                var want = row.Model.Label + " —— " + hint;
                if (_hintText.text != want) _hintText.text = want;
                return;
            }
        }

        private static Text CreateText(string name, Transform parent, string content, Font font, int size,
            Color color, TextAnchor anchor = TextAnchor.UpperLeft, FontStyle style = FontStyle.Normal)
        {
            var text = UiBuilder.CreateText(name, parent, content, font, size, color, anchor, style);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.supportRichText = false;
            return text;
        }

        private static RectTransform BuildArrow(Transform parent, Font font, float x, string label, Color color)
        {
            var image = UiBuilder.CreateImage("Arrow", parent, color);
            // 行高统一 46 后，加减按钮垂直居中在行内，不再按旧的 58 偏移。
            UiBuilder.Place(image.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-x, 0f), new Vector2(24f, 24f));
            var text = CreateText("Label", image.transform, label, font, 14, AceTheme.TextMain, TextAnchor.MiddleCenter);
            UiBuilder.Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
            return image.rectTransform;
        }

        private static void RefreshRowValues()
        {
            foreach (var row in Rows)
            {
                // 开关行：直接读布尔状态上色与定位。
                //
                // 原先是解析 GetValue() 返回的 "[ 开 ]" / "[ 关 ]" 字符串，
                // 靠 text.Contains("开") 判断颜色 —— 靠中文字符猜状态：
                // 任何含「开」字的文案（如「开局后几秒内不许开会」）都会让它变绿。
                // 现在开关行不再产生文本，改由 GetState() 直接给状态。
                if (row.SwitchTrack != null)
                {
                    var on = row.Model.GetState != null && row.Model.GetState();
                    row.SwitchTrack.color = on ? AceTheme.SwitchOn : AceTheme.SwitchOff;

                    if (row.SwitchKnob != null)
                    {
                        var target = on ? SwitchKnobOnX : SwitchKnobOffX;
                        var p = row.SwitchKnob.anchoredPosition;

                        // **到位就停手。**
                        // Mathf.Lerp 是渐近的，永远收敛不到目标值 ——
                        // 原来的写法每 0.2 秒都会写一次 anchoredPosition，
                        // 每次写入都标脏整个 Canvas。20 个开关行 × 5Hz = 每秒上百次重建，
                        // 即「UI 严重卡顿、游戏正常」的主因。
                        if (Mathf.Abs(p.x - target) < 0.5f)
                        {
                            if (!Mathf.Approximately(p.x, target))
                            {
                                p.x = target;
                                row.SwitchKnob.anchoredPosition = p;
                            }
                        }
                        else
                        {
                            p.x = Mathf.Lerp(p.x, target, 0.35f);
                            row.SwitchKnob.anchoredPosition = p;
                        }
                    }
                    continue;
                }

                if (row.SliderRect != null)
                {
                    var fraction = SettingsLayout.Normalize(row.Model.ReadNumber(), row.Model.Min, row.Model.Max);
                    var width = row.SliderRect.rect.width;

                    // 同上：RectTransform 赋值必标脏，值没变就别写。
                    var fillWidth = width * fraction;
                    if (!Mathf.Approximately(row.SliderFill.sizeDelta.x, fillWidth))
                        row.SliderFill.sizeDelta = new Vector2(fillWidth, 6f);

                    if (!Mathf.Approximately(row.SliderKnob.anchoredPosition.x, fillWidth))
                        row.SliderKnob.anchoredPosition = new Vector2(fillWidth, 0f);
                }
                if (row.Model.GetValue == null || row.Value == null) continue;
                var text = row.Model.GetValue();
                row.Value.text = text;

                // 非开关行按「这一行的性质」上色，不再靠文案里的字眼猜。
                row.Value.color = row.Model.Kind == RowKind.Info
                    ? AceTheme.TextDim
                    : AceTheme.Accent;
            }
        }

        /// <summary>提示底部「已保存」，几秒后自动恢复。</summary>
        private static void FlashSaved() => _savedFlashUntil = Time.time + 2.5f;

        // ================= 页面内容 =================

        /// <summary>
        /// 按页组装设置行。
        ///
        /// 拆成每页一个方法：原先是一个 143 行的 switch，新增一个配置项就要在
        /// 大函数里找位置，且六页内容混在一起没法单独看。
        /// </summary>
        private static List<SettingRow> BuildPageModel(int page)
        {
            var cfg = AntiCheatRuntime.Config;
            if (cfg == null) return new List<SettingRow>();

            switch (page)
            {
                case 0: return BuildGeneralPage(cfg);
                case 1: return BuildMovementPage(cfg);
                case 2: return BuildDispositionPage(cfg);
                case 3: return BuildVentPage(cfg);
                case 5: return BuildToolsPage(cfg);
                default: return BuildAboutPage();
            }
        }

        /// <summary>页 0「开着什么」：总开关与界面显示。</summary>
        private static List<SettingRow> BuildGeneralPage(AntiCheatConfig cfg)
        {
            var list = new List<SettingRow>();
            list.Add(Toggle("静态插件扫描", cfg.EnableStaticScan,
                "看看别人装了什么作弊插件。几乎不会误判，建议一直开着。"));
            list.Add(Toggle("运动学检测", cfg.EnableBehaviorScan,
                "定时记录每个人的位置，抓突然消失和跑得比正常人快。"));
            list.Add(Toggle("动作合法性检测", cfg.EnableEventScan,
                "检查击杀、爬管道这些动作在当前状态下能不能做。"));
            list.Add(Toggle("穿墙检测", cfg.EnableWallClipCheck,
                "会额外吃一点性能，网络卡时容易误判。认准作弊用上面那几项就够。"));
            list.Add(Toggle("屏幕通知", cfg.ShowNotifications,
                "命中检测规则时在屏幕上方弹一条通知。"));
            list.Add(Toggle("启动动画", cfg.ShowDesktopSplash,
                "进游戏时在桌面右下角弹一下 Apex Cheat Ender 的加载动画。"));
            list.Add(Toggle("ACE 用户标记", cfg.AcePresenceEnabled,
                "跟同样装了 Apex Cheat Ender 的人互相认一下，在对方名字上加个标记。"));
            list.Add(Info("ACE 标记文本", "改标记文字请直接编辑配置文件 apex.cheat.ender.cfg。",
                () => string.IsNullOrEmpty(cfg.AcePresenceTag.Value) ? "（默认）" : "已自定义"));
            list.Add(Toggle("非法破坏检测", cfg.SabotageCheck, "检查破坏者角色、会议状态和目标范围。"));
            list.Add(Toggle("角色能力检测", cfg.RoleActionCheck, "检查变形、保护等角色能力是否合法。"));
            list.Add(Toggle("聊天频率与内容检测", cfg.ChatCheck, "检查聊天频率和消息内容。"));
            list.Add(Toggle("昵称合法性检测", cfg.NameCheck, "检查空昵称、超长昵称和控制字符。"));
            list.Add(Toggle("详细日志", cfg.VerboseLogging,
                "只在怀疑误判、想查原因时开。日志会长得很快，平时关着。"));
            list.Add(Toggle("聊天内容本地提示", cfg.ShowChatAbuseNotice,
                "仅本地提示 1.2 秒，Esc 关闭；可能误报，不记作弊证据、不踢人。"));
            list.Add(Info("触发关键词", "要改词表请直接编辑配置文件 apex.cheat.ender.cfg 里的「疑似骂人关键词」。",
                () => (cfg.ChatAbuseKeywords.Value ?? string.Empty)
                    .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Length + " 个词"));
            return list;
        }

        /// <summary>页 1「跑多快算作弊」：阈值类参数，误判了就在这里调松。</summary>
        private static List<SettingRow> BuildMovementPage(AntiCheatConfig cfg)
        {
            var list = new List<SettingRow>();
            list.Add(Number("瞬移判定距离", cfg.TeleportMinDistance, 0.5f, 0.5f, 20f,
                "一次记录里位置突然变了这么多就是瞬移。正常走路一秒走不了这么远，几乎不会误判。"));
            list.Add(Number("速度上限倍率", cfg.MaxSpeedTolerance, 0.1f, 1.0f, 5.0f,
                "1.0 是完全不放水。正常建议 1.5 到 1.8。朋友被误判就往大了调。"));
            list.Add(Number("超速确认次数", cfg.SpeedStrikeCount, 1f, 1f, 20f,
                "偶尔超一下可能是卡了。连续超这么多次才算作弊证据。", true));
            list.Add(Number("位移抖动容差", cfg.PositionJitterTolerance, 0.05f, 0f, 2f,
                "小于这个距离当成网络延迟，不算作弊。网络差就往上调。"));
            list.Add(Number("采样间隔", cfg.SampleInterval, 0.01f, 0.02f, 1.0f,
                "越小抓得越紧，也越吃性能。0.1 是推荐值，觉得卡就调到 0.2。"));
            list.Add(Number("开局宽限期", cfg.RoundStartGracePeriod, 1f, 0f, 30f,
                "对局刚开始大家都在传送，这段时间不判定，避免误报。"));
            list.Add(Number("击杀距离容差", cfg.KillDistanceTolerance, 0.25f, 0f, 5f,
                "游戏设置的击杀距离之外再放宽这么多。调太小会漏掉远程击杀挂。"));
            list.Add(Number("击杀冷却容差", cfg.KillCooldownTolerance, 0.05f, 0f, 5f,
                "内鬼杀人冷却是 25 秒。能提前这么多秒再杀就是绕过冷却。"));
            list.Add(Number("任务速度倍率", cfg.TaskSpeedTolerance, 0.1f, 1.0f, 5f,
                "调太小会把边走边做任务的正常玩家误判成外挂。"));
            list.Add(Number("任务提交距离容差", cfg.RemoteTaskTolerance, 0.5f, 0f, 10f,
                "站在任务点附近这么远之内算完成。"));
            list.Add(Number("会议位移容差", cfg.MeetingMoveTolerance, 0.25f, 0f, 5f,
                "开会期间所有人都该站在会议桌附近，走太远就是有问题。"));
            list.Add(Number("聊天频率上限", cfg.ChatRateLimit, 1f, 3f, 50f,
                "超过这个数量算刷屏。正常聊天很难达到 8 条。", true));
            list.Add(Number("昵称长度上限", cfg.NameMaxLength, 1f, 5f, 60f,
                "超过算异常。游戏原生上限是 10，这里留了余量。", true));
            return list;
        }

        /// <summary>页 2「抓到怎么办」：处置方式与记录。</summary>
        private static List<SettingRow> BuildDispositionPage(AntiCheatConfig cfg)
        {
            var list = new List<SettingRow>();
            list.Add(Action("处置方式", "命中规则之后具体做什么。建议先选「警告」观察一阵，确认没误判再用更重的。",
                () =>
                {
                    cfg.DispositionMode.Value = DispositionModes.Next(cfg.DispositionMode.Value);
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
                () => "[ " + cfg.DispositionMode.Value + " ]"));
            list.Add(Toggle("自动执行", cfg.AllowAutoKick,
                "命中确定性规则就自动踢，不用你点确认。先只记录更保险。"));
            list.Add(Toggle("记录房间成员", cfg.RecordPlayerHistory,
                "写进 PlayerHistory.txt，方便事后查谁来过。"));
            list.Add(Toggle("记录判定结果", cfg.RecordCheatHistory,
                "写进 CheatHistory.txt，含昵称和命中的具体规则。万一误判了，这里就是翻案证据。"));

            // 手动踢人：列出房间里**所有人**，不只是命中规则的。
            // 之前只列命中者，等于「想踢个没被抓到的捣乱分子」做不到。
            var verdicts = AntiCheatRuntime.Verdicts;
            list.Add(Info("手动踢人", "下面是房间里所有人，房主可以直接踢出。命中过的会标注风险等级。",
                () => GameBridge.IsHost ? "你是房主" : "你不是房主，只能看"));

            foreach (var player in GameBridge.GetPlayers())
            {
                if (player == null) continue;
                var pid = GameBridge.GetPlayerId(player);
                if (pid < 0) continue;
                if (GameBridge.GetLocalPlayer() != null &&
                    GameBridge.GetPlayerId(GameBridge.GetLocalPlayer()) == pid) continue;   // 不列自己

                var name = GameBridge.GetPlayerName(player);

                var level = RiskLevel.Normal;
                var count = 0;
                if (verdicts != null)
                {
                    foreach (var v in verdicts.RankedVerdicts())
                    {
                        if (v.PlayerId != pid) continue;
                        level = v.EvaluateLevel();
                        count = v.EvidenceCount;
                        break;
                    }
                }

                var hint = count > 0
                    ? $"命中 {count} 条规则，风险等级 {level}。"
                    : "目前没有命中任何规则。";
                if (!GameBridge.IsHost) hint += "你需要是房主才能踢人。";

                list.Add(Action("踢出 " + name, hint,
                    () =>
                    {
                        if (!GameBridge.IsHost) return;

                        // 点击时才解析目标，**不能**在构建列表时把 clientId 捕获进闭包。
                        // 列表打开后玩家进出会重新分配 id / clientId，
                        // 用旧值会踢到别人头上 —— 这是踢人这种不可逆操作最不能犯的错。
                        //
                        // 同时校验玩家号与昵称：任一与构建时对不上就放弃，
                        // 宁可这次不踢，也不能踢错。
                        var clientId = -1;
                        var matched = false;
                        foreach (var current in GameBridge.GetPlayers())
                        {
                            if (current == null) continue;
                            if (GameBridge.GetPlayerId(current) != pid) continue;
                            if (GameBridge.GetPlayerName(current) != name) return;   // 号对上了但换人了
                            matched = true;
                            clientId = GameBridge.GetClientIdByPlayerId(pid);
                            break;
                        }
                        if (!matched || clientId < 0) return;

                        var banned = DispositionModes.ShouldBan(cfg.DispositionMode.Value);
                        if (GameBridge.KickPlayer(clientId, banned))
                            AntiCheatRuntime.Log?.LogWarning("[处置] 已手动踢出「" + name + "」。");
                    },
                    () => GameBridge.IsHost ? "点击踢出" : "需要房主"));

                // ── 一键封禁 ──
                // 抓到了却要手抄好友码去改配置文件，等于没抓到。
                // 这里直接把人加进本地名单，写进配置立刻生效。
                var banName = name;
                var banPlayer = player;
                list.Add(Action("封禁 " + banName,
                    "把这个人加入封禁名单，写进配置文件并立刻生效。"
                    + "没有好友码或平台 ID 的玩家无法封禁 —— 那种条目会误伤所有同名玩家。",
                    () =>
                    {
                        var code = GameBridge.GetFriendCode(banPlayer);
                        var puid = GameBridge.GetPuid(banPlayer);

                        cfg.BanListExtra.Value = Core.BanListDb.AppendEntry(
                            cfg.BanListExtra.Value, banName, code, puid, "手动封禁");

                        // 只有填了写入令牌的机器才推服务器；其余只写本地。
                        var token = cfg.BanListWriteToken?.Value;
                        if (!string.IsNullOrWhiteSpace(token))
                            Core.BanListRemote.PushBan(cfg.BanListEndpoint.Value, token,
                                new Core.BanEntry { Name = banName, Code = code, Puid = puid, Reason = "手动封禁" });

                        AntiCheatRuntime.ApplyConfigChange();
                        FlashSaved();
                        RefreshRowValues();
                    },
                    () =>
                    {
                        var code = GameBridge.GetFriendCode(banPlayer);
                        var puid = GameBridge.GetPuid(banPlayer);
                        return (code.Length == 0 && puid.Length == 0) ? "无好友码/平台ID，无法封禁" : "加入本地名单";
                    }));
            }
            return list;
        }

        /// <summary>页 3「爬管道」：通风管 / 滑索 / 网络防护。</summary>
        private static List<SettingRow> BuildVentPage(AntiCheatConfig cfg)
        {
            var list = new List<SettingRow>();
            list.Add(Toggle("非内鬼使用通风管", cfg.VentNonImpostor,
                "只有内鬼能爬管道。其他人爬了就是开了挂。"));
            list.Add(Toggle("远距离使用通风管", cfg.VentRemote,
                "离管道口很远却爬进去了。"));
            list.Add(Toggle("伪造通风管编号", cfg.VentForgedId,
                "发了一个根本不存在的管道编号，说明在改游戏数据。"));
            list.Add(Toggle("强制他人离开通风管", cfg.VentForceOther,
                "用漏洞让别人被强行拉进管道。"));
            list.Add(Toggle("滑索异常使用", cfg.ZiplineAbuse,
                "强行滑索，或者开会的时候滑索。"));
            list.Add(Toggle("会议期间使用通风管", cfg.VentDuringMeeting,
                "开会期间所有人都被定在会议桌，这时候爬不了。"));
            list.Add(Number("通风管距离容差", cfg.VentDistanceTolerance, 0.5f, 0.5f, 10f,
                "离管道口这么远之内算正常使用。太严格会误判站在旁边的人。"));
            list.Add(Toggle("RPC 洪水检测", cfg.RpcFloodDetection,
                "有人疯狂发数据包会让全房卡顿。"));
            list.Add(Number("RPC 频率上限", cfg.RpcRateLimit, 5f, 5f, 200f,
                "网络差的房间可能会误判，遇到误报就往上调。", true));
            list.Add(Toggle("异常位置同步检测", cfg.SnapRateDetection,
                "反复强制同步位置，是瞬移挂的典型做法。"));
            list.Add(Number("位置同步上限", cfg.SnapRateLimit, 1f, 1f, 40f,
                "正常对局几乎不会出现连续的位置强制同步。", true));
            list.Add(Toggle("开局误报检测", cfg.BlockEarlyMeeting,
                "开局几秒内疯狂开会举报的，通常是在刷屏或者想破坏游戏。"));
            list.Add(Number("开局保护期", cfg.EarlyMeetingGrace, 1f, 0f, 60f,
                "这段时间内开会举报会被拦下来。"));
            list.Add(Toggle("超大包检测", cfg.OversizedPacketCheck,
                "异常大的数据包，可能是想拖垮所有人。"));
            return list;
        }

        /// <summary>页「关于」：版本与帮助。</summary>
        /// <summary>
        /// 「记录与工具」页。
        ///
        /// **主体是「谁被检测了、几次、犯了什么」** —— 这是最该一眼看到的信息。
        /// 该页此前包含 18 行「分组映射」等配置项，淹没了主要信息。
        /// 现在按「检测汇总 → 历史事件 → 工具 → 分组映射」排，重要程度递减。
        /// </summary>
        private static List<SettingRow> BuildToolsPage(AntiCheatConfig cfg)
        {
            var list = new List<SettingRow>();

            // ================= 检测汇总 =================
            var verdicts = AntiCheatRuntime.Verdicts;
            var flagged = new List<PlayerVerdict>();
            if (verdicts != null)
                foreach (var v in verdicts.RankedVerdicts())
                    if (v.Evidence.Count > 0) flagged.Add(v);

            var totalHits = 0;
            foreach (var v in flagged) totalHits += v.Evidence.Count;

            list.Add(Info("检测汇总",
                "本局各玩家命中的规则与次数，按次数从多到少。只统计当前这一局。",
                () => flagged.Count == 0 ? "暂无命中" : $"{flagged.Count} 人 · {totalHits} 次"));

            if (flagged.Count == 0)
            {
                list.Add(Info("本局暂无检测记录",
                    "有玩家触发规则后会出现在这里，并显示他命中了哪几条、各几次。",
                    () => "—"));
            }
            else
            {
                foreach (var verdict in flagged)
                {
                    var v = verdict;

                    // 按规则聚合计数：同一条规则命中多次只占一行，后面跟次数
                    var counts = new Dictionary<ViolationKind, int>();
                    foreach (var e in v.Evidence)
                        counts[e.Kind] = counts.TryGetValue(e.Kind, out var c) ? c + 1 : 1;

                    // 最近一条证据原文 —— 悬停时在底部描述栏显示，即「命中内容」
                    var latest = v.Evidence[v.Evidence.Count - 1].Detail;

                    list.Add(Info(v.Name,
                        latest,
                        () => $"{v.EvaluateLevel()} · 共 {v.Evidence.Count} 次"));

                    foreach (var pair in counts)
                    {
                        var kind = pair.Key;
                        list.Add(Info("      " + kind,
                            RuleHintOf(kind),
                            () => (counts.TryGetValue(kind, out var c) ? c : 0) + " 次"));
                    }
                }
            }

            // ================= 历史事件 =================
            list.Add(Info("历史事件",
                "跨回合累计，最多保留 256 条；用来回看整晚的情况。",
                () => (verdicts?.History.Count ?? 0) + " 条"));

            var filtered = new List<Violation>();
            if (verdicts != null)
                filtered.AddRange(RuleGroups.Filter(verdicts.History, _historyGroup, cfg.RuleGroupMapping.Value));

            var pages = Math.Max(1, (filtered.Count + 7) / 8);
            _historyPage = Math.Min(_historyPage, pages - 1);

            list.Add(Action("历史分类",
                "点击切换分类过滤；只影响这里显示的历史，不影响检测。",
                () =>
                {
                    _historyGroup = (_historyGroup + 1) % (RuleGroups.Names.Length + 1);
                    _historyPage = 0;
                    BuildRows(_currentPage);
                },
                () => _historyGroup == 0 ? "全部" : RuleGroups.Names[_historyGroup - 1]));

            list.Add(Action("历史翻页",
                "每页八条，点击翻到下一页。",
                () =>
                {
                    _historyPage = (_historyPage + 1) % pages;
                    BuildRows(_currentPage);
                },
                () => $"{_historyPage + 1}/{pages}"));

            for (var i = _historyPage * 8; i < Math.Min(filtered.Count, (_historyPage + 1) * 8); i++)
            {
                var item = filtered[i];
                list.Add(Action($"{item.PlayerName} · {item.Kind}",
                    item.Detail,
                    () =>
                    {
                        _selectedDetail = item.Detail;
                        BuildRows(_currentPage);
                    },
                    () => "详情"));
            }

            if (_selectedDetail != null)
                list.Add(Info("选中详情", _selectedDetail, () => "本地记录"));

            // ================= 工具 =================
            var presets = new[] { "保守", "标准", "严格" };
            list.Add(Action("阈值预设",
                "切换并应用阈值；自动踢人设置保持原样。",
                () =>
                {
                    _preset = (_preset + 1) % presets.Length;
                    cfg.ApplyPreset(_preset);
                    AntiCheatRuntime.ApplyConfigChange();
                    FlashSaved();
                },
                () => presets[_preset]));

            list.Add(Action("恢复安全默认",
                "五秒内再次点击确认；保留自动踢人和自定义文本。",
                () =>
                {
                    if (!RestoreConfirmation.Confirm(Time.unscaledTime)) return;
                    cfg.RestoreSafeDefaults();
                    AntiCheatRuntime.ApplyConfigChange();
                    FlashSaved();
                },
                () => RestoreConfirmation.Armed(Time.unscaledTime) ? "再次点击确认" : "恢复默认"));

            list.Add(Action("导出脱敏诊断",
                "导出内容不含昵称、聊天原文或配置。",
                () => { _exportStatus = AntiCheatRuntime.ExportDiagnostics(); },
                () => _exportStatus));

            list.Add(Info("性能探针",
                "低频采样，只在本地显示。",
                () => $"{AntiCheatRuntime.CurrentFps:F0} FPS"));

            list.Add(Toggle("重复消息提示", cfg.ShowRepeatedChatNotice,
                "默认关闭；按发送者计数，10秒冷却，不计作弊、不踢人。"));
            list.Add(Number("重复确认次数", cfg.RepeatedChatThreshold, 1f, 3f, 10f,
                "同一发送者10秒内连续发送相同消息的次数。", true));

            // ================= 分组映射 =================
            //
            // 原来这里把**每一条规则**都列成一行（40 多行），每条还带一句说明 ——
            // 一屏塞不下、翻半天看不到别的，而实际上几乎没人会去改。
            // 现仅保留一行提示：默认分组已足够，如需调整请修改配置文件。
            list.Add(Info("规则分组",
                "给每条规则指定所属分组，只影响「历史事件」的分类过滤，不影响检测和处置。"
                + "默认分组已经够用；要调整请改配置文件里的 RuleGroupMapping。",
                () => RuleGroups.Names.Length + " 个分组"));

            return list;
        }

        /// <summary>规则的中文说明；用于悬停时解释「这条规则管的是什么」。</summary>
        private static string RuleHintOf(ViolationKind kind)
        {
            switch (kind)
            {
                // 静态层
                case ViolationKind.KnownCheatPlugin: return "命中了已知作弊插件的特征。";
                case ViolationKind.UnknownPlugin: return "加载了不在白名单内的可疑插件。";
                case ViolationKind.MemoryTamper: return "检测到内存注入或方法被改写的痕迹。";
                // 运动层
                case ViolationKind.Teleport: return "单次位移大到不可能由正常移动产生。";
                case ViolationKind.SpeedHack: return "移动速度持续超过本局设置的上限。";
                case ViolationKind.WallClip: return "运动轨迹穿过了墙体。";
                // 事件层
                case ViolationKind.KillTooFar: return "击杀距离超过设置允许的最大距离。";
                case ViolationKind.KillCooldownBypass: return "两次击杀的间隔短于角色冷却时间。";
                case ViolationKind.KillWhileNotImpostor: return "非内鬼身份执行了击杀。";
                case ViolationKind.TaskTooFast: return "两次任务间隔短于物理上可能的最短时间。";
                case ViolationKind.RemoteTask: return "在离任务点很远的位置提交了任务。";
                case ViolationKind.IllegalVent: return "不具备能力或距离过远时使用了通风管。";
                case ViolationKind.GhostAction: return "已死亡的玩家仍执行了活人动作。";
                // 会议层
                case ViolationKind.MoveDuringMeeting: return "会议期间仍在移动（连续采样确认）。";
                case ViolationKind.IllegalMeetingAction: return "会议期间的投票或报告行为异常。";
                case ViolationKind.EarlyMeeting: return "开局保护期内发起会议或报告尸体。";
                // 破坏层
                case ViolationKind.SabotageWhileNotImpostor: return "非内鬼阵营触发了破坏系统。";
                case ViolationKind.SabotageDuringMeeting: return "会议期间触发了破坏系统。";
                case ViolationKind.InvalidSabotageTarget: return "破坏目标越界，疑似改包。";
                // 通讯层
                case ViolationKind.ChatFlood: return "聊天发送频率异常（刷屏）。";
                case ViolationKind.IllegalChat: return "聊天内容非法（空、超长或含控制字符）。";
                case ViolationKind.IllegalName: return "昵称非法（空、超长或含控制字符）。";
                // 角色动作层
                case ViolationKind.IllegalShapeshift: return "不具备变形能力的角色执行了变形。";
                case ViolationKind.IllegalProtect: return "不具备保护能力的角色执行了保护。";
                // 通风管 / 滑索
                case ViolationKind.VentForgedId: return "使用了不存在的通风管编号。";
                case ViolationKind.VentDuringMeeting: return "会议期间使用了通风管。";
                case ViolationKind.VentForceOther: return "非房主强制把他人踢出通风管。";
                case ViolationKind.ZiplineAbuse: return "滑索使用时机非法或坐标越界。";
                // 名单层
                case ViolationKind.BannedPlayer: return "命中内置封禁名单（按好友码 / 平台 ID 匹配，不是名字）。";
                // 网络层
                case ViolationKind.InvalidRpc: return "收到参数非法的 RPC 调用。";
                case ViolationKind.StateDesync: return "上报状态与权威状态长期不一致。";
                case ViolationKind.OversizedPacket: return "收到异常大的数据包。";
                default: return null;
            }
        }

        private static List<SettingRow> BuildAboutPage()
        {
            var list = new List<SettingRow>();
            // 版本号直接取常量，避免与 csproj 的 <Version> 各写各的（曾因此对不上）
            list.Add(Info("插件名称", "Apex Cheat Ender",
                () => AntiCheatPlugin.PluginVersion));
            list.Add(Info("快捷键", "Insert 打开或关闭设置；按住标题栏拖动窗口。", () => ""));
            list.Add(Info("改配置要不要重启", "不用。用记事本改完保存，游戏里几秒内自动生效。", () => ""));
            list.Add(Info("配置文件在哪", "BepInEx/config/apex.cheat.ender.cfg", () => ""));
            list.Add(Info("被误判了怎么办", "去「跑多快算作弊」页把倍数往松了调，或者关掉对应那条检测。", () => ""));
            list.Add(Info("运行环境", "BepInEx 6 / IL2CPP / .NET 6", () => ""));
            list.Add(Info("参考", "判定思路借鉴了 Amethyst 反作弊的设计。", () => ""));
            return list;
        }

        // ================= 行工厂 =================

        private static SettingRow Toggle(string label, BepInEx.Configuration.ConfigEntry<bool> entry, string hint)
        {
            return new SettingRow
            {
                Label = label,
                Hint = hint,
                Kind = RowKind.Toggle,
                GetState = () => entry.Value,
                OnToggle = () =>
                {
                    entry.Value = !entry.Value;
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
            };
        }

        private static SettingRow Number(
            string label,
            BepInEx.Configuration.ConfigEntry<float> entry,
            float step, float min, float max, string hint, bool integer = false)
        {
            return new SettingRow
            {
                Label = label,
                Hint = hint,
                Kind = RowKind.Number,
                Min = min, Max = max, Step = step, Integer = integer,
                ReadNumber = () => entry.Value,
                WriteNumber = value =>
                {
                    if (entry.Value == value) return;
                    entry.Value = value;
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
                GetValue = () => entry.Value.ToString(integer ? "F0" : "0.##"),
                OnMinus = () =>
                {
                    entry.Value = Mathf.Clamp(entry.Value - step, min, max);
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
                OnPlus = () =>
                {
                    entry.Value = Mathf.Clamp(entry.Value + step, min, max);
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
            };
        }

        private static SettingRow Number(
            string label,
            BepInEx.Configuration.ConfigEntry<int> entry,
            float step, float min, float max, string hint, bool integer = true)
        {
            return new SettingRow
            {
                Label = label,
                Hint = hint,
                Kind = RowKind.Number,
                Min = min, Max = max, Step = 1f, Integer = true,
                ReadNumber = () => entry.Value,
                WriteNumber = value =>
                {
                    var next = (int)value;
                    if (entry.Value == next) return;
                    entry.Value = next;
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
                GetValue = () => entry.Value.ToString(),
                OnMinus = () =>
                {
                    entry.Value = Mathf.Clamp(entry.Value - 1, (int)min, (int)max);
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
                OnPlus = () =>
                {
                    entry.Value = Mathf.Clamp(entry.Value + 1, (int)min, (int)max);
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
            };
        }

        private static SettingRow Action(string label, string hint, Action onClick, Func<string> value)
        {
            return new SettingRow
            {
                Label = label,
                Hint = hint,
                Kind = RowKind.Action,
                GetValue = value,
                OnToggle = onClick,
            };
        }

        private static SettingRow Info(string label, string hint, Func<string> value)
        {
            return new SettingRow
            {
                Label = label,
                Hint = hint,
                Kind = RowKind.Info,
                GetValue = value,
            };
        }

        // ================= 交互 =================

        /// <summary>设置界面当前是否打开。</summary>
        public static bool IsOpen => _visible;

        public static void Tick(bool toggleRequested)
        {
            if (!_built || _root == null) return;

            if (toggleRequested)
            {
                RestoreConfirmation.Cancel();
                _visible = !_visible;
                _root.SetActive(_visible);
                _activeSlider = null;
                _windowDragging = _scrollDragging = _pressArmed = _gestureMoved = false;
                if (_visible)
                {
                    _nextRowRefresh = 0f;     // 打开时立刻刷一次
                    _scrollVelocity = 0f;
                    _lastMouseY = Input.mousePosition.y;
                }
            }

            if (!_visible) return;

            ClampWindow();
            // 缩放手柄优先级最高：它压在窗口右下角，不能被拖动或点击抢走。
            if (HandleResize()) return;
            if (HandleWindowDrag()) return;

            if (!HandleSlider())
            {
                HandleClick();
                HandleScroll();
            }

            ApplyScroll();

            // 节流到 5Hz：配置行值不需要每帧重算。
            // 原先每帧遍历所有行 + 字符串 Contains，是可见期间的持续垃圾来源。
            if (Time.time >= _nextRowRefresh)
            {
                _nextRowRefresh = Time.time + 0.2f;
                RefreshRowValues();
            }

            RefreshFooter();
        }

        private static void ClampWindow()
        {
            var scale = Mathf.Min(1f, Mathf.Min(Screen.width / WindowWidth, Screen.height / WindowHeight));

            // 同上：RectTransform 赋值必然标脏，没变就别写。
            if (!Mathf.Approximately(scale, _appliedWindowScale))
            {
                _appliedWindowScale = scale;
                _windowRect.localScale = new Vector3(scale, scale, 1f);
            }

            var x = Mathf.Max(0f, (Screen.width - WindowWidth * scale) / 2f);
            var y = Mathf.Max(0f, (Screen.height - WindowHeight * scale) / 2f);
            var p = _windowRect.anchoredPosition;
            var target = new Vector2(Mathf.Clamp(p.x, -x, x), Mathf.Clamp(p.y, -y, y));
            if ((target - p).sqrMagnitude < 0.0001f) return;

            _appliedWindowPos = target;
            _windowRect.anchoredPosition = target;
        }

        /// <summary>
        /// 右下角手柄拖拽缩放。
        ///
        /// 拖拽过程中每 120 毫秒重建一次布局，松手时再重建一次并写回配置。
        /// 为什么不在每一帧重建：重建会销毁并重新创建整窗的 GameObject，
        /// 每帧做一次等于每秒造几千个对象，纯浪费。
        /// </summary>
        private static bool HandleResize()
        {
            var mouse = Input.mousePosition;

            if (Input.GetMouseButtonDown(0) && _resizeGrip != null &&
                RectTransformUtility.RectangleContainsScreenPoint(_resizeGrip, mouse, null))
            {
                _windowResizing = true;
                _pressArmed = _scrollDragging = false;
                _scrollVelocity = 0f;
                _resizeStartMouse = new Vector2(mouse.x, mouse.y);
                _resizeStartWidth = WindowWidth;
                _resizeStartHeight = WindowHeight;
            }

            if (!_windowResizing) return false;

            // 鼠标位移要换算回「未缩放前的窗口单位」，否则窗口被自动缩小时手感会飘。
            var scale = Mathf.Max(0.01f, _windowRect.localScale.x);
            var delta = new Vector2(mouse.x, mouse.y) - _resizeStartMouse;
            WindowWidth = Mathf.Clamp(_resizeStartWidth + delta.x / scale, MinWindowWidth, MaxWindowWidth);
            WindowHeight = Mathf.Clamp(_resizeStartHeight + delta.y / scale, MinWindowHeight, MaxWindowHeight);

            var now = Time.unscaledTime;
            if (now >= _nextLiveRelayout)
            {
                _nextLiveRelayout = now + 0.12f;
                Relayout();
            }

            if (Input.GetMouseButtonUp(0) || !Input.GetMouseButton(0))
            {
                _windowResizing = false;
                Relayout();
                PersistWindowSize();
            }

            return true;
        }

        /// <summary>把当前尺寸写回配置，下次启动沿用。</summary>
        private static void PersistWindowSize()
        {
            var cfg = AntiCheatRuntime.Config;
            if (cfg == null) return;

            var w = Mathf.RoundToInt(WindowWidth);
            var h = Mathf.RoundToInt(WindowHeight);
            if (cfg.SettingsWindowWidth.Value == w && cfg.SettingsWindowHeight.Value == h) return;

            cfg.SettingsWindowWidth.Value = w;
            cfg.SettingsWindowHeight.Value = h;
            FlashSaved();
            AntiCheatRuntime.ApplyConfigChange();
        }

        /// <summary>
        /// 按当前 WindowWidth / WindowHeight 重建整窗。
        ///
        /// 骨架和每一行的位置都是在构建时按尺寸算好的，改尺寸只能整窗重建 ——
        /// 这也是拖拽时要做节流的原因。
        /// </summary>
        private static void Relayout()
        {
            if (_canvasRoot == null) return;

            var page = _currentPage;
            var wasVisible = _visible;

            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _resizeGrip = null;
            _built = false;
            Rows.Clear();
            Tabs.Clear();

            EnsureBuilt(_canvasRoot);
            if (page > 0) SwitchPage(page);
            if (_root != null) _root.SetActive(wasVisible);
        }

        private static bool HandleWindowDrag()
        {
            var mouse = Input.mousePosition;
            if (Input.GetMouseButtonDown(0) &&
                RectTransformUtility.RectangleContainsScreenPoint(_titleRect, mouse, null))
            {
                _windowDragging = true;
                _pressArmed = _scrollDragging = false;
                _scrollVelocity = 0f;
                _dragStart = new Vector2(mouse.x, mouse.y);
                _windowStart = _windowRect.anchoredPosition;
            }
            if (!_windowDragging) return false;
            _windowRect.anchoredPosition = _windowStart + new Vector2(mouse.x, mouse.y) - _dragStart;
            ClampWindow();
            if (Input.GetMouseButtonUp(0) || !Input.GetMouseButton(0)) _windowDragging = false;
            return true; // 包括松开帧，防止一次拖动再触发行点击。
        }

        // ================= 滚动 =================

        /// <summary>
        /// 处理滚轮与拖拽。只更新偏移量，实际位移统一交给 <see cref="ApplyScroll"/>。
        /// 设置项最多的一页有 14 行（870px），视口只有约 520px，必须能滚。
        /// </summary>
        private static void HandleScroll()
        {
            var max = SettingsLayout.MaxScroll(_contentHeight, _viewportHeight);
            if (max <= 0f)
            {
                _scrollOffset = 0f;
                return;
            }

            var mouse = Input.mousePosition;
            var overViewport = _viewport != null &&
                RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null);

            if (overViewport)
            {
                // 滚轮
                var wheel = Input.mouseScrollDelta.y;
                if (Mathf.Abs(wheel) > 0.01f)
                {
                    _scrollOffset -= wheel * 60f;
                    _scrollVelocity = 0f;
                }

                // 拖拽：按住左键上下拖
                if (_scrollDragging && _gestureMoved && Input.GetMouseButton(0))
                {
                    var dy = (mouse.y - _lastMouseY) / Mathf.Max(_windowRect.localScale.y, 0.01f);
                    if (Mathf.Abs(dy) > 0.01f)
                    {
                        _scrollOffset += dy;
                        _scrollVelocity = dy / Mathf.Max(Time.deltaTime, 0.0001f);
                    }
                }
            }

            _lastMouseY = mouse.y;

            // 松手后的惯性衰减（视觉上更顺滑，且衰减很快不会失控）
            if (!Input.GetMouseButton(0) && Mathf.Abs(_scrollVelocity) > 1f)
            {
                _scrollOffset += _scrollVelocity * Time.deltaTime;
                _scrollVelocity *= 0.85f;
            }
            else if (!Input.GetMouseButton(0))
            {
                _scrollVelocity = 0f;
            }

            _scrollOffset = Mathf.Clamp(_scrollOffset, 0f, max);
        }

        /// <summary>
        /// 应用滚动偏移。
        ///
        /// **值没变就绝不写 RectTransform。**
        /// Unity 里给 RectTransform 赋值一定会标脏，进而触发**整个 Canvas 重新生成网格**。
        /// 设置页有数百个元素，每帧重建即导致 UI 严重卡顿 ——
        /// 游戏逻辑本身未受影响，仅本模组 UI 空转。
        /// </summary>
        private static void ApplyScroll()
        {
            if (_contentArea == null) return;
            var max = SettingsLayout.MaxScroll(_contentHeight, _viewportHeight);
            _scrollOffset = Mathf.Clamp(_scrollOffset, 0f, max);

            if (Mathf.Approximately(_scrollOffset, _appliedScroll)) return;
            _appliedScroll = _scrollOffset;
            _contentArea.anchoredPosition = new Vector2(0f, _scrollOffset);
        }

        /// <summary>上一次真正写进 RectTransform 的滚动值，用于跳过无变化的写入。</summary>
        private static float _appliedScroll = float.NaN;

        /// <summary>上一次真正写进去的窗口缩放/位置，用于跳过无变化的写入。</summary>
        private static float _appliedWindowScale = float.NaN;
        private static Vector2 _appliedWindowPos = new Vector2(float.NaN, float.NaN);

        /// <summary>鼠标是否落在滚动视口内。行命中测试必须先过这一关，
        /// 否则被裁剪掉（滚出视口）的行仍可能在屏幕外的位置被点到。</summary>
        private static bool MouseOverViewport(Vector3 mouse) =>
            _viewport != null && RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null);

        public static bool IsVisible => _visible;

        private static void RefreshFooter()
        {
            if (_footerText == null) return;

            // 文本内容不变就不写 —— 虽然 Unity 的 Text.text setter 自带相等判断，
            // 但每帧拼一次字符串再比一次毫无意义，这里直接按状态短路。
            var flashing = Time.time < _savedFlashUntil;
            if (flashing == _footerFlashing) return;
            _footerFlashing = flashing;

            if (flashing)
            {
                _footerText.text = "已保存 · 立刻生效，不用重启";
                _footerText.color = AceTheme.Success;
                return;
            }

            _footerText.color = AceTheme.TextDim;
            _footerText.text = "改动会立即保存并生效。配置文件在 BepInEx/config/apex.cheat.ender.cfg，"
                             + "用记事本改完保存也会自动生效。";
        }

        /// <summary>上一次刷新时是否处于「已保存」闪烁态，用于跳过无变化的写入。</summary>
        private static bool _footerFlashing = true;

        private static bool HandleSlider()
        {
            var mouse = Input.mousePosition;
            if (_activeSlider == null && Input.GetMouseButtonDown(0) && MouseOverViewport(mouse))
            {
                foreach (var row in Rows)
                {
                    if (row.SliderRect == null || !RectTransformUtility.RectangleContainsScreenPoint(row.SliderRect, mouse, null)) continue;
                    _activeSlider = row;
                    _pressArmed = _scrollDragging = false;
                    _scrollVelocity = 0f;
                    break;
                }
            }
            if (_activeSlider == null) return false;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_activeSlider.SliderRect, mouse, null, out var point))
            {
                var bounds = _activeSlider.SliderRect.rect;
                var model = _activeSlider.Model;
                model.WriteNumber(SettingsLayout.SliderValue((point.x - bounds.xMin) / bounds.width,
                    model.Min, model.Max, model.Step, model.Integer));
                RefreshRowValues();
            }
            if (Input.GetMouseButtonUp(0) || !Input.GetMouseButton(0)) _activeSlider = null;
            return true;
        }

        private static void HandleClick()
        {
            // 改成「按下记录 + 松开触发」：
            // 若直接使用 GetMouseButtonDown，用户按住拖拽滚动时会误触行内开关。
            if (Input.GetMouseButtonDown(0))
            {
                _pressPos = Input.mousePosition;
                _pressArmed = RectTransformUtility.RectangleContainsScreenPoint(_windowRect, _pressPos, null);
                _scrollDragging = MouseOverViewport(_pressPos);
                _gestureMoved = false;
                _scrollVelocity = 0f;
                _lastMouseY = _pressPos.y;
                return;
            }

            if (_pressArmed && (Mathf.Abs(Input.mousePosition.x - _pressPos.x) > 6f ||
                Mathf.Abs(Input.mousePosition.y - _pressPos.y) > 6f)) _gestureMoved = true;
            if (!Input.GetMouseButtonUp(0)) return;
            _scrollDragging = false;
            if (!_pressArmed) return;
            _pressArmed = false;

            var mouse = Input.mousePosition;

            // 移动超过阈值 → 判定为拖拽滚动，不触发点击
            if (_gestureMoved) return;

            foreach (var tab in Tabs)
            {
                if (!RectTransformUtility.RectangleContainsScreenPoint(tab.Rect, mouse, null)) continue;
                if (tab.Index != _currentPage) SwitchPage(tab.Index);
                return;
            }

            foreach (var tab in SubTabs)
            {
                if (!RectTransformUtility.RectangleContainsScreenPoint(tab.Rect, mouse, null) ||
                    !RectTransformUtility.RectangleContainsScreenPoint(tab.Rect, _pressPos, null)) continue;
                SelectedSubTabs[_currentPage] = tab.Index;
                BuildSubTabs(_currentPage);
                BuildRows(_currentPage);
                return;
            }
            if (MouseOverViewport(mouse))
            {
                // 分组不再可折叠，这里只需在悬停时把该行说明显示到底部描述栏。
                UpdateHoverHint(mouse);
            }

            // 行命中必须先确认鼠标在视口内 —— 被裁剪掉（滚出视口）的行，
            // 其 RectTransform 的屏幕坐标仍在视口之外，不判这一条会点错。
            if (!MouseOverViewport(mouse)) return;

            foreach (var row in Rows)
            {
                if (!RectTransformUtility.RectangleContainsScreenPoint(row.Rect, _pressPos, null)) continue;
                if (row.Model.Kind == RowKind.Number)
                {
                    if (row.MinusRect != null &&
                        RectTransformUtility.RectangleContainsScreenPoint(row.MinusRect, mouse, null))
                    {
                        row.Model.OnMinus?.Invoke();
                        RefreshRowValues();
                        return;
                    }

                    if (row.PlusRect != null &&
                        RectTransformUtility.RectangleContainsScreenPoint(row.PlusRect, mouse, null))
                    {
                        row.Model.OnPlus?.Invoke();
                        RefreshRowValues();
                        return;
                    }

                    continue;
                }

                if (!RectTransformUtility.RectangleContainsScreenPoint(row.Rect, mouse, null)) continue;
                HandleRowClick(row, mouse);
                return;
            }
        }

        private static void HandleRowClick(RowEntry row, Vector3 mouse)
        {
            var model = row.Model;

            switch (model.Kind)
            {
                case RowKind.Toggle:
                case RowKind.Action:
                    model.OnToggle?.Invoke();
                    break;

                case RowKind.Number:
                    // 数字行已经由 HandleClick 直接命中减号/加号，
                    // 点到中间数值区域不做任何操作。
                    break;
            }

            RefreshRowValues();
        }
    }
}
