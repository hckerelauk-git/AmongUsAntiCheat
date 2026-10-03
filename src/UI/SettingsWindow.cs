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
        private const float WindowWidth = 800f;
        private const float WindowHeight = 560f;
        private const float TitleBarHeight = 64f;
        private const float TabColumnWidth = 170f;
        private const float FooterHeight = 46f;
        private const float ContentPadding = 28f;

        // ================= 开关控件尺寸 =================
        // 轨道 52×26，滑块直径 20，左右各留 3px 内边距。
        // 滑块的 x 用它中心到轨道左边缘的距离表示（anchoredPosition.x）。
        private const float SwitchKnobOffX = 13f;   // 3 + 20/2
        private const float SwitchKnobOnX = 39f;    // 52 - 3 - 20/2

        // ================= 侧边栏尺寸 =================
        // 页签 44 高、间隔 52：留 8px 空隙，比原来 42/48 更透气。
        private const float TabHeight = 44f;
        private const float TabSpacing = 52f;

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
        private static int _currentPage;

        private static readonly List<TabEntry> Tabs = new List<TabEntry>();
        private static readonly List<RowEntry> Rows = new List<RowEntry>();

        private static readonly List<GameObject> Cards = new List<GameObject>();
        private static readonly List<RowEntry> CardHeaders = new List<RowEntry>();
        private static readonly List<TabEntry> SubTabs = new List<TabEntry>();
        private static readonly HashSet<string> CollapsedCards = new HashSet<string>();
        private static readonly int[] SelectedSubTabs = new int[6];
        private static readonly DoubleClickConfirmation RestoreConfirmation = new DoubleClickConfirmation();
        private static string _exportStatus = "点击导出";
        private static string _selectedDetail;
        private static int _historyPage;
        private static int _historyGroup;
        private static int _preset;
        private static RectTransform _subTabArea;
        private static RowEntry _activeSlider;
        private static readonly List<TextInputField> TextInputs = new List<TextInputField>();
        private static RectTransform _windowRect;
        private static RectTransform _titleRect;
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
        }

        // ================= 行模型 =================

        private enum RowKind { Toggle, Number, Info, Action, TextField }

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
            /// 靠中文字符猜状态，改一句文案就会错。改由这里直接给布尔值。
            /// </summary>
            public Func<bool> GetState;

            public Action OnToggle;
            public Action OnMinus;
            public Action OnPlus;
            public Func<string> GetText;
            public Action<string> SetText;
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
        /// 用形状而不是内置图片：插件要保证「一个 dll 就是完整插件」，
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
            "总开关和界面显示。一般不用改。",
            "按移动、击杀任务、会议聊天分类调整检测阈值。",
            "抓到人之后怎么处置，命中的规则怎么记。",
            "检查爬通风管、滑索、刷数据包这类动作。",
            "版本信息和快捷键。",
            "本地事件、风险详情、阈值预设和脱敏诊断；不上传数据。",
        };

        // ================= 构建 =================

        public static void EnsureBuilt(Transform canvasRoot)
        {
            if (_built || canvasRoot == null) return;
            _built = true;

            var font = UiBuilder.LoadFont(13);

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

            // ---- 顶部强调条 ----
            // 原先是横贯整个窗口宽度的亮青色条（1060×3），在一块深色卡片上
            // 非常抢眼，把注意力从内容上抢走了。改成只覆盖标题区宽度的一小段，
            // 当「标题下的点睛线」用 —— 有设计感，但不喧宾夺主。
            var topBar = UiBuilder.CreateImage("TopBar", _root.transform, AceTheme.Primary);
            topBar.sprite = AceTheme.Card(2, 0, Color.white, Color.white);
            topBar.type = Image.Type.Sliced;
            UiBuilder.Place(topBar.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(18f, -14f), new Vector2(132f, 2f));

            var shieldTex = AceTheme.MakeShield(48, AceTheme.Primary, AceTheme.Accent);
            var shield = UiBuilder.CreateImage("Shield", _root.transform, Color.white, shieldTex);
            UiBuilder.Place(shield.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(18f, -18f), new Vector2(32f, 32f));

            var title = CreateText("Title", _root.transform,
                "APEX CHEAT ENDER", font, 16, AceTheme.TextMain, TextAnchor.UpperLeft, FontStyle.Bold);
            UiBuilder.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(62f, -17f), new Vector2(320f, 26f));

            // 这句是普通说明，不是「操作成功」的状态提示，用绿色（Success）会
            // 让人以为刚刚发生了什么。改用次要文字色。
            var subtitle = CreateText("SubTitle", _root.transform,
                "设置", font, 11, AceTheme.TextDim);
            UiBuilder.Place(subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(62f, -40f), new Vector2(340f, 18f));

            var hint = CreateText("Hint", _root.transform,
                "Insert 关闭", font, 11, AceTheme.TextDim, TextAnchor.UpperRight);
            UiBuilder.Place(hint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(WindowWidth - 180f, -20f), new Vector2(160f, 20f));

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

            var tabBg = UiBuilder.CreateImage("TabBg", _root.transform, AceTheme.TabColumnBg);
            UiBuilder.Place(tabBg.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0f, (FooterHeight - TitleBarHeight) / 2f),
                new Vector2(TabColumnWidth, WindowHeight - TitleBarHeight - FooterHeight));

            var divLeft = UiBuilder.CreateImage("DivLeft", _root.transform, AceTheme.Border);
            UiBuilder.Place(divLeft.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(TabColumnWidth, (FooterHeight - TitleBarHeight) / 2f),
                new Vector2(1f, WindowHeight - TitleBarHeight - FooterHeight));

            // ---- 页标题与页说明：固定在窗口上，不随内容滚动 ----
            var textLeft = TabColumnWidth + ContentPadding;
            _pageTitleText = CreateText("PageTitle", _root.transform,
                "", font, 15, AceTheme.Accent, TextAnchor.UpperLeft, FontStyle.Bold);
            UiBuilder.Place(_pageTitleText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(textLeft, -TitleBarHeight - 10f), new Vector2(500f, 22f));

            _pageHintText = CreateText("PageHint", _root.transform,
                "", font, 11, AceTheme.TextDim, TextAnchor.UpperLeft);
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

            _footerText = CreateText("Footer", _root.transform,
                "", font, 11, AceTheme.TextDim);
            UiBuilder.Place(_footerText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(18f, 10f), new Vector2(WindowWidth - 36f, 20f));

            var statusBar = UiBuilder.CreateImage("StatusBar", _root.transform, AceTheme.Success);
            statusBar.sprite = AceTheme.Card(2, 0, Color.white, Color.white);
            statusBar.type = Image.Type.Sliced;
            UiBuilder.Place(statusBar.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(10f, 10f), new Vector2(WindowWidth - 20f, 3f));

            // 导航置于固定背景之后，且完全脱离 RectMask2D。
            tabAreaNode.transform.SetAsLastSibling();
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
                    new Vector2(20f, 0f), new Vector2(18f, 18f));

                var label = CreateText("TabLabel" + i, node.transform,
                    TabNames[i], font, 13, AceTheme.TextDim, TextAnchor.MiddleLeft);
                UiBuilder.Place(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(48f, 0f), new Vector2(TabColumnWidth - 78f, 26f));

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
            var names = page == 1 ? new[] { "移动", "击杀 / 任务", "会议 / 聊天" }
                : page == 5 ? new[] { "记录与工具" } : new[] { "全部设置" };
            var width = (WindowWidth - TabColumnWidth - ContentPadding * 2f - (names.Length - 1) * 6f) / names.Length;
            for (var i = 0; i < names.Length; i++)
            {
                var image = UiBuilder.CreateImage("SubTab" + i, _subTabArea, i == SelectedSubTabs[page] ? AceTheme.TabActiveBg : AceTheme.RowBgA);
                UiBuilder.Place(image.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(i * (width + 6f), 0f), new Vector2(width, 30f));
                var text = CreateText("Label", image.transform, names[i], UiBuilder.LoadFont(13), 12,
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
            foreach (var card in Cards)
            {
                card.SetActive(false);
                UnityEngine.Object.Destroy(card);
            }
            Cards.Clear();
            CardHeaders.Clear();
            Rows.Clear();
            TextInputs.Clear();

            // 换页回到顶部：否则会停留在上一页的滚动位置，看起来像"内容没了"
            _scrollOffset = 0f;
            _scrollVelocity = 0f;

            var model = BuildPageModel(page);
            var font = UiBuilder.LoadFont(13);
            var contentWidth = WindowWidth - TabColumnWidth - ContentPadding * 2f;

            var cursor = 0f;
            var group = -1;
            RectTransform cardBody = null;
            var bodyCursor = 38f;
            var collapsed = false;
            for (var i = 0; i < model.Count; i++)
            {
                var row = model[i];
                var rowGroup = SettingsLayout.Group(page, i);
                if (page == 1 && rowGroup != SelectedSubTabs[page]) continue;
                if (group != rowGroup)
                {
                    group = rowGroup;
                    var key = page + ":" + group;
                    collapsed = CollapsedCards.Contains(key);
                    var bodyHeight = 0f;
                    for (var j = i; j < model.Count && SettingsLayout.Group(page, j) == group; j++)
                        bodyHeight += SettingsLayout.RowHeight(model[j].Kind == RowKind.Number);
                    var height = SettingsLayout.CardHeight(bodyHeight, collapsed);
                    var card = UiBuilder.CreateImage("Group" + group, _contentArea, Color.white);
                    card.sprite = AceTheme.Card(8, 1, AceTheme.RowBgA, AceTheme.RowBorder);
                    card.type = Image.Type.Sliced;
                    UiBuilder.Place(card.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                        new Vector2(0f, -cursor), new Vector2(contentWidth, height));
                    Cards.Add(card.gameObject);
                    cardBody = card.rectTransform;
                    var header = CreateText("GroupTitle", card.transform,
                        (collapsed ? ">  " : "v  ") + (page < SettingsLayout.GroupNames.Length ? SettingsLayout.GroupNames[page][group] : "本地记录与工具"), font, 13, AceTheme.Accent,
                        TextAnchor.MiddleLeft, FontStyle.Bold);
                    UiBuilder.Place(header.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                        new Vector2(14f, 0f), new Vector2(contentWidth - 28f, 38f));
                    CardHeaders.Add(new RowEntry { Rect = header.rectTransform, Model = new SettingRow
                    {
                        OnToggle = () =>
                        {
                            if (!CollapsedCards.Add(key)) CollapsedCards.Remove(key);
                            BuildRows(page);
                        }
                    } });
                    cursor += height + 10f;
                    bodyCursor = 38f;
                }
                if (collapsed) continue;
                var rowHeight = SettingsLayout.RowHeight(row.Kind == RowKind.Number);
                var y = -bodyCursor;
                bodyCursor += rowHeight;
                var node = UiBuilder.CreateNode("Row" + i, cardBody);
                var rect = node.GetComponent<RectTransform>();
                UiBuilder.Place(rect, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(0f, y), new Vector2(contentWidth, rowHeight - 4f));

                // 圆角行底板 + 斑马纹
                var bg = node.AddComponent<Image>();
                bg.sprite = AceTheme.Card(3, 0,
                    i % 2 == 0 ? AceTheme.RowBgA : AceTheme.RowBgB,
                    AceTheme.RowBorder);
                bg.type = Image.Type.Sliced;
                bg.raycastTarget = false;

                // 标题与说明之间留 4px 间隙：说明字号更小、颜色更暗，
                // 靠「字号 + 灰度 + 间距」三重区分层级，不再靠挤在一起
                var label = CreateText("RowLabel" + i, node.transform,
                    row.Label, font, 13, AceTheme.TextMain, TextAnchor.UpperLeft);
                UiBuilder.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(16f, -8f), new Vector2(contentWidth - (row.Kind == RowKind.TextField ? 290f : row.Kind == RowKind.Info || row.Kind == RowKind.Action ? 150f : 100f), 20f));
                label.horizontalOverflow = HorizontalWrapMode.Wrap;
                label.verticalOverflow = VerticalWrapMode.Truncate;
                label.supportRichText = false;

                var hint = CreateText("RowHint" + i, node.transform,
                    row.Hint ?? string.Empty, font, 11, AceTheme.TextDim, TextAnchor.UpperLeft);
                UiBuilder.Place(hint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(16f, -29f), new Vector2(contentWidth - (row.Kind == RowKind.TextField ? 290f : row.Kind == RowKind.Info || row.Kind == RowKind.Action ? 150f : 100f), 26f));
                hint.horizontalOverflow = HorizontalWrapMode.Wrap;
                hint.verticalOverflow = VerticalWrapMode.Truncate;
                hint.supportRichText = false;

                if (row.Kind == RowKind.TextField)
                {
                    // 密钥行：右侧挂一个真正的输入控件。
                    var hostNode = UiBuilder.CreateNode("InputHost", node.transform);
                    var hostRect = hostNode.GetComponent<RectTransform>();
                        UiBuilder.Place(hostRect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                        new Vector2(-16f, 0f), new Vector2(260f, 30f));

                    TextInputs.Add(new TextInputField(hostRect, row.GetText, row.SetText, false, "点这里输入文字"));
                    Rows.Add(new RowEntry { Model = row, Rect = rect, Background = bg });
                    continue;
                }

                if (row.Kind == RowKind.Number)
                {
                    var value = CreateText("RowValue" + i, node.transform,
                        "", font, 13, AceTheme.Accent, TextAnchor.MiddleRight, FontStyle.Bold);
                    UiBuilder.Place(value.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                        new Vector2(-16f, -8f), new Vector2(76f, 20f));
                    var inset = row.Integer ? 52f : 22f;
                    var slider = UiBuilder.CreateNode("SliderHit", node.transform).GetComponent<RectTransform>();
                    UiBuilder.Place(slider, new Vector2(0f, 1f), new Vector2(0f, 1f),
                        new Vector2(inset, -58f), new Vector2(contentWidth - inset * 2f, 24f));
                    var track = UiBuilder.CreateImage("Track", slider, AceTheme.SwitchOff);
                    UiBuilder.Place(track.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                        Vector2.zero, new Vector2(contentWidth - inset * 2f, 6f));
                    var fill = UiBuilder.CreateImage("Fill", slider, AceTheme.Accent);
                    UiBuilder.Place(fill.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                        Vector2.zero, new Vector2(0f, 6f));
                    var knob = UiBuilder.CreateImage("SliderKnob", slider, AceTheme.SwitchKnob);
                    knob.sprite = AceTheme.Dot(14, Color.white);
                    UiBuilder.Place(knob.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
                        Vector2.zero, new Vector2(14f, 14f));
                    Rows.Add(new RowEntry
                    {
                        Model = row, Rect = rect, Background = bg, Value = value,
                        SliderRect = slider, SliderFill = fill.rectTransform, SliderKnob = knob.rectTransform,
                        MinusRect = row.Integer ? BuildArrow(node.transform, font, 14f, "<", AceTheme.BtnMinusBg) : null,
                        PlusRect = row.Integer ? BuildArrow(node.transform, font, contentWidth - 38f, ">", AceTheme.BtnPlusBg) : null,
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
                        new Vector2(-18f, -2f), new Vector2(52f, 26f));

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
                        Background = bg,
                        SwitchTrack = track,
                        SwitchKnob = knobRect,
                    });
                    continue;
                }

                var normalValue = CreateText("RowValue" + i, node.transform,
                    "", font, 13, AceTheme.Accent, TextAnchor.MiddleRight, FontStyle.Bold);
                UiBuilder.Place(normalValue.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                    new Vector2(-16f, 0f), new Vector2(110f, 30f));
                Rows.Add(new RowEntry
                {
                    Model = row,
                    Rect = rect,
                    Background = bg,
                    Value = normalValue,
                });
            }

            _contentHeight = cursor + ContentBottomPadding;
            _contentArea.sizeDelta = new Vector2(0f, Mathf.Max(_contentHeight, _viewportHeight));
            _contentArea.anchoredPosition = Vector2.zero;
            RefreshRowValues();
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
            UiBuilder.Place(image.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(x, -58f), new Vector2(24f, 24f));
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
                        // 滑块滑动做插值，切换时有过渡而不是瞬移
                        var p = row.SwitchKnob.anchoredPosition;
                        p.x = Mathf.Lerp(p.x, target, 0.35f);
                        row.SwitchKnob.anchoredPosition = p;
                    }
                    continue;
                }

                if (row.SliderRect != null)
                {
                    var fraction = SettingsLayout.Normalize(row.Model.ReadNumber(), row.Model.Min, row.Model.Max);
                    var width = row.SliderRect.rect.width;
                    row.SliderFill.sizeDelta = new Vector2(width * fraction, 6f);
                    row.SliderKnob.anchoredPosition = new Vector2(width * fraction, 0f);
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
            list.Add(Toggle("扫描作弊插件", cfg.EnableStaticScan,
                "看看别人装了什么作弊插件。几乎不会误判，建议一直开着。"));
            list.Add(Toggle("检测瞬移和超速", cfg.EnableBehaviorScan,
                "定时记录每个人的位置，抓突然消失和跑得比正常人快。"));
            list.Add(Toggle("检查动作是否合法", cfg.EnableEventScan,
                "检查击杀、爬管道这些动作在当前状态下能不能做。"));
            list.Add(Toggle("检测穿墙", cfg.EnableWallClipCheck,
                "会额外吃一点性能，网络卡时容易误判。认准作弊用上面那几项就够。"));
            list.Add(Toggle("顶部弹出提醒", cfg.ShowNotifications,
                "命中检测规则时在屏幕上方弹一条通知。"));
            list.Add(Toggle("开机启动动画", cfg.ShowDesktopSplash,
                "进游戏时在桌面右下角弹一下 Apex Cheat Ender 的加载动画。"));
            list.Add(Toggle("标记同装 ACE 的玩家", cfg.AcePresenceEnabled,
                "跟同样装了 Apex Cheat Ender 的人互相认一下，在对方名字上加个标记。"));
            list.Add(new SettingRow
            {
                Label = "标记写成什么",
                Hint = "显示在对方名字后面的文字。默认带表情符号；"
                     + "游戏里若显示成方块，改成纯文字（比如 [ACE]）即可。",
                Kind = RowKind.TextField,
                GetValue = () => string.IsNullOrEmpty(cfg.AcePresenceTag.Value)
                    ? "（默认）"
                    : "已自定义",
                GetText = () => cfg.AcePresenceTag.Value ?? string.Empty,
                SetText = v =>
                {
                    cfg.AcePresenceTag.Value = string.IsNullOrEmpty(v)
                        ? Core.AcePresence.DefaultTag
                        : v;
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
            });
            list.Add(Toggle("抓非法破坏", cfg.SabotageCheck, "检查破坏者角色、会议状态和目标范围。"));
            list.Add(Toggle("抓角色动作异常", cfg.RoleActionCheck, "检查变形、保护等角色能力是否合法。"));
            list.Add(Toggle("抓聊天刷屏和非法消息", cfg.ChatCheck, "检查聊天频率和消息内容。"));
            list.Add(Toggle("抓非法昵称", cfg.NameCheck, "检查空昵称、超长昵称和控制字符。"));
            list.Add(Toggle("输出详细日志", cfg.VerboseLogging,
                "只在怀疑误判、想查原因时开。日志会长得很快，平时关着。"));
            list.Add(Toggle("疑似骂人短提示", cfg.ShowChatAbuseNotice,
                "仅本地提示 1.2 秒，Esc 关闭；可能误报，不记作弊证据、不踢人。"));
            list.Add(new SettingRow
            {
                Label = "疑似骂人关键词",
                Hint = "逗号分隔，留空禁用匹配。引用也可能误报，避免泛词。",
                Kind = RowKind.TextField,
                GetText = () => cfg.ChatAbuseKeywords.Value ?? string.Empty,
                SetText = value =>
                {
                    cfg.ChatAbuseKeywords.Value = value ?? string.Empty;
                    FlashSaved();
                },
            });
            return list;
        }

        /// <summary>页 1「跑多快算作弊」：阈值类参数，误判了就在这里调松。</summary>
        private static List<SettingRow> BuildMovementPage(AntiCheatConfig cfg)
        {
            var list = new List<SettingRow>();
            list.Add(Number("一下挪多远算瞬移", cfg.TeleportMinDistance, 0.5f, 0.5f, 20f,
                "一次记录里位置突然变了这么多就是瞬移。正常走路一秒走不了这么远，几乎不会误判。"));
            list.Add(Number("允许比正常快几倍", cfg.MaxSpeedTolerance, 0.1f, 1.0f, 5.0f,
                "1.0 是完全不放水。正常建议 1.5 到 1.8。朋友被误判就往大了调。"));
            list.Add(Number("超速几次才记下来", cfg.SpeedStrikeCount, 1f, 1f, 20f,
                "偶尔超一下可能是卡了。连续超这么多次才算作弊证据。", true));
            list.Add(Number("多小的位移算抖动", cfg.PositionJitterTolerance, 0.05f, 0f, 2f,
                "小于这个距离当成网络延迟，不算作弊。网络差就往上调。"));
            list.Add(Number("看位置的间隔", cfg.SampleInterval, 0.01f, 0.02f, 1.0f,
                "越小抓得越紧，也越吃性能。0.1 是推荐值，觉得卡就调到 0.2。"));
            list.Add(Number("开局后先不管几秒", cfg.RoundStartGracePeriod, 1f, 0f, 30f,
                "对局刚开始大家都在传送，这段时间不判定，避免误报。"));
            list.Add(Number("隔多远能砍人", cfg.KillDistanceTolerance, 0.25f, 0f, 5f,
                "游戏设置的击杀距离之外再放宽这么多。调太小会漏掉远程击杀挂。"));
            list.Add(Number("冷却能提前多久", cfg.KillCooldownTolerance, 0.05f, 0f, 5f,
                "内鬼杀人冷却是 25 秒。能提前这么多秒再杀就是绕过冷却。"));
            list.Add(Number("做任务允许快几倍", cfg.TaskSpeedTolerance, 0.1f, 1.0f, 5f,
                "调太小会把边走边做任务的正常玩家误判成外挂。"));
            list.Add(Number("隔多远能交任务", cfg.RemoteTaskTolerance, 0.5f, 0f, 10f,
                "站在任务点附近这么远之内算完成。"));
            list.Add(Number("开会时允许走多远", cfg.MeetingMoveTolerance, 0.25f, 0f, 5f,
                "开会期间所有人都该站在会议桌附近，走太远就是有问题。"));
            list.Add(Number("聊天 10 秒最多几条", cfg.ChatRateLimit, 1f, 3f, 50f,
                "超过这个数量算刷屏。正常聊天很难达到 8 条。", true));
            list.Add(Number("昵称最长多少字", cfg.NameMaxLength, 1f, 5f, 60f,
                "超过算异常。游戏原生上限是 10，这里留了余量。", true));
            return list;
        }

        /// <summary>页 2「抓到怎么办」：处置方式与记录。</summary>
        private static List<SettingRow> BuildDispositionPage(AntiCheatConfig cfg)
        {
            var list = new List<SettingRow>();
            list.Add(Action("动手的方式", "命中规则之后具体做什么。建议先选「警告」观察一阵，确认没误判再用更重的。",
                () =>
                {
                    cfg.DispositionMode.Value = DispositionModes.Next(cfg.DispositionMode.Value);
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
                () => "[ " + cfg.DispositionMode.Value + " ]"));
            list.Add(Toggle("自动踢人", cfg.AllowAutoKick,
                "命中确定性规则就自动踢，不用你点确认。先只记录更保险。"));
            list.Add(Toggle("记录谁进过房间", cfg.RecordPlayerHistory,
                "写进 PlayerHistory.txt，方便事后查谁来过。"));
            list.Add(Toggle("记录作弊判定", cfg.RecordCheatHistory,
                "写进 CheatHistory.txt，含昵称和命中的具体规则。万一误判了，这里就是翻案证据。"));

            // 当前命中列表 → 逐人提供「踢出」按钮
            var verdicts = AntiCheatRuntime.Verdicts;
            if (verdicts == null) return list;

            foreach (var v in verdicts.RankedVerdicts())
            {
                if (v.EvaluateLevel() == RiskLevel.Normal) continue;
                var pid = v.PlayerId;
                var name = v.Name;
                var detail = "命中 " + v.EvidenceCount + " 条规则"
                           + (GameBridge.IsHost ? "" : "，你需要是房主才能踢人。");

                list.Add(Action("踢出 " + name, detail,
                    () =>
                    {
                        if (!GameBridge.IsHost) return;
                        var clientId = GameBridge.GetClientIdByPlayerId(pid);
                        if (clientId < 0) return;
                        var banned = DispositionModes.ShouldBan(cfg.DispositionMode.Value);
                        if (GameBridge.KickPlayer(clientId, banned))
                            AntiCheatRuntime.Log?.LogWarning("[处置] 已手动踢出「" + name + "」。");
                    },
                    () => GameBridge.IsHost ? "[ 点击踢出 ]" : "[ 非房主 ]"));
            }
            return list;
        }

        /// <summary>页 3「爬管道」：通风管 / 滑索 / 网络防护。</summary>
        private static List<SettingRow> BuildVentPage(AntiCheatConfig cfg)
        {
            var list = new List<SettingRow>();
            list.Add(Toggle("抓普通人爬管道", cfg.VentNonImpostor,
                "只有内鬼能爬管道。其他人爬了就是开了挂。"));
            list.Add(Toggle("抓隔着屏幕爬管道", cfg.VentRemote,
                "离管道口很远却爬进去了。"));
            list.Add(Toggle("抓伪造管道编号", cfg.VentForgedId,
                "发了一个根本不存在的管道编号，说明在改游戏数据。"));
            list.Add(Toggle("抓强迫别人爬管道", cfg.VentForceOther,
                "用漏洞让别人被强行拉进管道。"));
            list.Add(Toggle("抓滑索滥用", cfg.ZiplineAbuse,
                "强行滑索，或者开会的时候滑索。"));
            list.Add(Toggle("抓开会时爬管道", cfg.VentDuringMeeting,
                "开会期间所有人都被定在会议桌，这时候爬不了。"));
            list.Add(Number("爬管道允许离多远", cfg.VentDistanceTolerance, 0.5f, 0.5f, 10f,
                "离管道口这么远之内算正常使用。太严格会误判站在旁边的人。"));
            list.Add(Toggle("抓数据包刷屏", cfg.RpcFloodDetection,
                "有人疯狂发数据包会让全房卡顿。"));
            list.Add(Number("10 秒最多几次数据包", cfg.RpcRateLimit, 5f, 5f, 200f,
                "网络差的房间可能会误判，遇到误报就往上调。", true));
            list.Add(Toggle("抓瞬移式位置同步", cfg.SnapRateDetection,
                "反复强制同步位置，是瞬移挂的典型做法。"));
            list.Add(Number("10 秒最多几次同步", cfg.SnapRateLimit, 1f, 1f, 40f,
                "正常对局几乎不会出现连续的位置强制同步。", true));
            list.Add(Toggle("抓开局乱开会", cfg.BlockEarlyMeeting,
                "开局几秒内疯狂开会举报的，通常是在刷屏或者想破坏游戏。"));
            list.Add(Number("开局后几秒内不许开会", cfg.EarlyMeetingGrace, 1f, 0f, 60f,
                "这段时间内开会举报会被拦下来。"));
            list.Add(Toggle("抓超大数据包", cfg.OversizedPacketCheck,
                "异常大的数据包，可能是想拖垮所有人。"));
            return list;
        }

        /// <summary>页「关于」：版本与帮助。</summary>
        private static List<SettingRow> BuildToolsPage(AntiCheatConfig cfg)
        {
            var list = new List<SettingRow>();
            var presets = new[] { "保守", "标准", "严格" };
            list.Add(Action("规则预设", "切换并应用阈值；自动踢人设置保持原样。", () =>
            {
                _preset = (_preset + 1) % presets.Length;
                cfg.ApplyPreset(_preset);
                AntiCheatRuntime.ApplyConfigChange();
                FlashSaved();
            }, () => presets[_preset]));
            list.Add(Action("恢复安全默认", "五秒内再次点击确认；保留自动踢人和自定义文本。", () =>
            {
                if (!RestoreConfirmation.Confirm(Time.unscaledTime)) return;
                cfg.RestoreSafeDefaults();
                AntiCheatRuntime.ApplyConfigChange();
                FlashSaved();
            }, () => RestoreConfirmation.Armed(Time.unscaledTime) ? "再次点击确认" : "恢复默认"));
            list.Add(Toggle("重复聊天本地提示", cfg.ShowRepeatedChatNotice,
                "默认关闭；按发送者计数，10秒冷却，不计作弊、不踢人。"));
            list.Add(Number("重复几次才提示", cfg.RepeatedChatThreshold, 1f, 3f, 10f,
                "同一发送者10秒内连续发送相同消息的次数。", true));
            foreach (ViolationKind kind in Enum.GetValues(typeof(ViolationKind)))
            {
                var rule = kind;
                list.Add(Action("分组 · " + rule, "点击依次切换六组并保存；仅影响历史分类。", () =>
                {
                    cfg.RuleGroupMapping.Value = RuleGroups.Cycle(rule, cfg.RuleGroupMapping.Value);
                    _historyPage = 0;
                    AntiCheatRuntime.ApplyConfigChange();
                    FlashSaved();
                    BuildRows(_currentPage);
                }, () => RuleGroups.Resolve(rule, cfg.RuleGroupMapping.Value)));
            }
            list.Add(Info("性能", "低频采样，只在本地显示。", () => $"{AntiCheatRuntime.CurrentFps:F0} FPS"));
            list.Add(Action("导出脱敏诊断", "不包含昵称、聊天原文或配置。", () =>
            {
                _exportStatus = AntiCheatRuntime.ExportDiagnostics();
            }, () => _exportStatus));
            list.Add(Action("历史分类", "使用本地规则分组映射过滤。", () =>
            {
                _historyGroup = (_historyGroup + 1) % (RuleGroups.Names.Length + 1);
                _historyPage = 0;
                BuildRows(_currentPage);
            }, () => _historyGroup == 0 ? "全部" : RuleGroups.Names[_historyGroup - 1]));
            var filtered = new List<Violation>();
            if (AntiCheatRuntime.Verdicts != null)
                filtered.AddRange(RuleGroups.Filter(AntiCheatRuntime.Verdicts.History, _historyGroup, cfg.RuleGroupMapping.Value));
            var pages = Math.Max(1, (filtered.Count + 7) / 8);
            _historyPage = Math.Min(_historyPage, pages - 1);
            list.Add(Action("历史分页", "每页八条，最多保留256条事件。", () =>
            {
                _historyPage = (_historyPage + 1) % pages;
                BuildRows(_currentPage);
            }, () => $"{_historyPage + 1}/{pages} · {filtered.Count}条"));
            for (var i = _historyPage * 8; i < Math.Min(filtered.Count, (_historyPage + 1) * 8); i++)
            {
                var item = filtered[i];
                list.Add(Action($"玩家 {item.PlayerId} · {item.Kind}", item.Severity.ToString(), () =>
                {
                    _selectedDetail = item.Detail;
                    BuildRows(_currentPage);
                }, () => "查看详情"));
            }
            list.Add(Info("命中详情", _selectedDetail ?? "点击历史条目查看依据。", () => "本地记录"));
            if (AntiCheatRuntime.Verdicts != null)
                foreach (var verdict in AntiCheatRuntime.Verdicts.RankedVerdicts())
                {
                    if (verdict.EvaluateLevel() == RiskLevel.Normal) continue;
                    var v = verdict;
                    list.Add(Info($"玩家 {v.PlayerId} 风险", "仅本地显示，不修改网络名字。", () => v.EvaluateLevel().ToString()));
                }
            return list;
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
            if (HandleWindowDrag()) return;

            // 输入控件先吃事件，避免点键盘时顺带把别的开关点了
            var inputCaptured = false;
            foreach (var input in TextInputs)
            {
                input.Tick();
                inputCaptured |= input.CapturesMouse;
            }

            if (!inputCaptured)
            {
                if (!HandleSlider())
                {
                    HandleClick();
                    HandleScroll();
                }
            }
            else
            {
                _pressArmed = _scrollDragging = false;
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
            _windowRect.localScale = new Vector3(scale, scale, 1f);
            var x = Mathf.Max(0f, (Screen.width - WindowWidth * scale) / 2f);
            var y = Mathf.Max(0f, (Screen.height - WindowHeight * scale) / 2f);
            var p = _windowRect.anchoredPosition;
            _windowRect.anchoredPosition = new Vector2(Mathf.Clamp(p.x, -x, x), Mathf.Clamp(p.y, -y, y));
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

        private static void ApplyScroll()
        {
            if (_contentArea == null) return;
            var max = SettingsLayout.MaxScroll(_contentHeight, _viewportHeight);
            _scrollOffset = Mathf.Clamp(_scrollOffset, 0f, max);
            _contentArea.anchoredPosition = new Vector2(0f, _scrollOffset);
        }

        /// <summary>鼠标是否落在滚动视口内。行命中测试必须先过这一关，
        /// 否则被裁剪掉（滚出视口）的行仍可能在屏幕外的位置被点到。</summary>
        private static bool MouseOverViewport(Vector3 mouse) =>
            _viewport != null && RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null);

        public static bool IsVisible => _visible;

        private static void RefreshFooter()
        {
            if (_footerText == null) return;

            if (Time.time < _savedFlashUntil)
            {
                _footerText.text = "已保存 · 立刻生效，不用重启";
                _footerText.color = AceTheme.Success;
                return;
            }

            _footerText.color = AceTheme.TextDim;
            _footerText.text = "改动会立即保存并生效。配置文件在 BepInEx/config/apex.cheat.ender.cfg，"
                             + "用记事本改完保存也会自动生效。";
        }

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
            // 若直接用 GetMouseButtonDown，用户想按住拖拽滚动时会顺带把行上的开关点掉。
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
                foreach (var header in CardHeaders)
                {
                    if (!RectTransformUtility.RectangleContainsScreenPoint(header.Rect, mouse, null) ||
                        !RectTransformUtility.RectangleContainsScreenPoint(header.Rect, _pressPos, null)) continue;
                    header.Model.OnToggle();
                    return;
                }
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
