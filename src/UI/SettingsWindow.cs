using System;
using System.Collections.Generic;
using ApexCheatEnder.Config;
using ApexCheatEnder.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ApexCheatEnder.UI
{
    /// <summary>
    /// 设置界面。
    ///
    /// 重写的四条理由（来自实际使用反馈）：
    ///   1. 页签名字全是术语，用户不知道点进去会看到什么
    ///   2. 每行只有个名字，改了会发生什么全靠猜
    ///   3. AI 那页有三个供应商循环切换、地址模型密钥三样都要填
    ///   4. 改完还得重启游戏才生效
    ///
    /// 对应的改法：页签换成大白话、每行下面配一句人话说明、
    /// AI 只留一个供应商且用户只需填密钥、改完立刻生效并在底部提示。
    ///
    /// 交互不使用 uGUI 的 Button/Toggle：它们的 onClick 要往 il2cpp
    /// 事件上挂托管委托，在动态注册的类型上不稳。这里每帧读鼠标位置，
    /// 用 RectTransformUtility 做命中测试，自己分发点击。
    /// </summary>
    internal static class SettingsWindow
    {
        private const float WindowWidth = 1080f;
        private const float WindowHeight = 700f;
        private const float TitleBarHeight = 64f;
        private const float TabColumnWidth = 220f;
        private const float FooterHeight = 46f;
        /// <summary>
        /// 单行高度。一行要放下「标题 + 说明」两段文字，58px 太挤：
        /// 标题 20px 紧贴说明 18px，再叠上不同分辨率下的字体缩放就会出现
        /// 视觉重叠。放宽到 74px，两段文字之间留出明显间隙。
        /// </summary>
        private const float RowHeight = 74f;
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

        /// <summary>行底板圆角半径。</summary>
        private const int RowRadius = 8;

        /// <summary>内容区顶部的起始偏移（给页标题与页说明留位置）。</summary>
        private const float ContentTopOffset = 58f;

        /// <summary>内容区底部留白，避免最后一行贴住 footer。</summary>
        private const float ContentBottomPadding = 12f;

        // ================= 状态 =================

        private static bool _built;
        private static bool _visible;
        private static GameObject _root;
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

        /// <summary>密钥输入控件，只在 AI 页存在。</summary>
        private static TextInputField _keyInput;

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
            "开着什么",
            "跑多快算作弊",
            "抓到怎么办",
            "爬管道",
            "AI 帮忙看",
            "关于",
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
            AceTheme.GlyphShape.Ring,       // AI 帮忙看：智能
            AceTheme.GlyphShape.Bars,       // 关于：条目
        };

        private static readonly string[] TabHints =
        {
            "总开关和界面显示。一般不用改。",
            "判定跑太快、瞬移的松紧程度。被误判了就往松了调。",
            "抓到人之后怎么处置，命中的规则怎么记。",
            "检查爬通风管、滑索、刷数据包这类动作。",
            "让大模型帮忙复核命中的规则。只需要填一把密钥。",
            "版本信息和快捷键。",
        };

        // ================= 构建 =================

        public static void EnsureBuilt(Transform canvasRoot)
        {
            if (_built || canvasRoot == null) return;
            _built = true;

            var font = UiBuilder.LoadFont(13);

            _root = UiBuilder.CreateNode("AceSettings", canvasRoot);
            var rootRect = _root.GetComponent<RectTransform>();
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

            var title = UiBuilder.CreateText("Title", _root.transform,
                "APEX CHEAT ENDER", font, 16, AceTheme.TextMain, TextAnchor.UpperLeft, FontStyle.Bold);
            UiBuilder.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(62f, -17f), new Vector2(320f, 26f));

            // 这句是普通说明，不是「操作成功」的状态提示，用绿色（Success）会
            // 让人以为刚刚发生了什么。改用次要文字色。
            var subtitle = UiBuilder.CreateText("SubTitle", _root.transform,
                "改动立刻生效，不用重启游戏", font, 11, AceTheme.TextDim);
            UiBuilder.Place(subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(250f, -20f), new Vector2(340f, 20f));

            var hint = UiBuilder.CreateText("Hint", _root.transform,
                "Insert 关闭", font, 11, AceTheme.TextDim, TextAnchor.UpperRight);
            UiBuilder.Place(hint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(WindowWidth - 180f, -20f), new Vector2(160f, 20f));

            var divTop = UiBuilder.CreateImage("DivTop", _root.transform, AceTheme.Border);
            UiBuilder.Place(divTop.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, -TitleBarHeight), new Vector2(WindowWidth, 1f));

            // ---- 左侧页签栏 ----
            var tabBg = UiBuilder.CreateImage("TabBg", _root.transform, AceTheme.TabColumnBg);
            UiBuilder.Place(tabBg.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0f, -(FooterHeight / 2f) + (TitleBarHeight / 2f)),
                new Vector2(TabColumnWidth, WindowHeight - TitleBarHeight - FooterHeight));

            var divLeft = UiBuilder.CreateImage("DivLeft", _root.transform, AceTheme.Border);
            UiBuilder.Place(divLeft.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(TabColumnWidth, -(FooterHeight / 2f) + (TitleBarHeight / 2f)),
                new Vector2(1f, WindowHeight - TitleBarHeight - FooterHeight));

            // ---- 页标题与页说明：固定在窗口上，不随内容滚动 ----
            var textLeft = TabColumnWidth + ContentPadding;
            _pageTitleText = UiBuilder.CreateText("PageTitle", _root.transform,
                "", font, 15, AceTheme.Accent, TextAnchor.UpperLeft, FontStyle.Bold);
            UiBuilder.Place(_pageTitleText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(textLeft, -TitleBarHeight - 10f), new Vector2(500f, 22f));

            _pageHintText = UiBuilder.CreateText("PageHint", _root.transform,
                "", font, 11, AceTheme.TextDim, TextAnchor.UpperLeft);
            UiBuilder.Place(_pageHintText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(textLeft, -TitleBarHeight - 32f), new Vector2(760f, 18f));

            // ---- 滚动视口：裁剪 + 承载行内容 ----
            // 设置项最多的一页有 14 行（需 870px），而可用高度只有约 520px。
            // 没有这层裁剪，最后 5~6 个选项会渲染到窗口外，用户看不见也点不到。
            var viewportNode = UiBuilder.CreateNode("Viewport", _root.transform);
            _viewport = viewportNode.GetComponent<RectTransform>();
            var vpW = WindowWidth - TabColumnWidth - ContentPadding * 2f;
            var vpH = WindowHeight - TitleBarHeight - 60f - FooterHeight - ContentBottomPadding;
            UiBuilder.Place(_viewport, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(textLeft, -TitleBarHeight - 60f), new Vector2(vpW, vpH));
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

            _footerText = UiBuilder.CreateText("Footer", _root.transform,
                "", font, 11, AceTheme.TextDim);
            UiBuilder.Place(_footerText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(18f, 10f), new Vector2(WindowWidth - 36f, 20f));

            var statusBar = UiBuilder.CreateImage("StatusBar", _root.transform, AceTheme.Success);
            statusBar.sprite = AceTheme.Card(2, 0, Color.white, Color.white);
            statusBar.type = Image.Type.Sliced;
            UiBuilder.Place(statusBar.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(10f, 10f), new Vector2(WindowWidth - 20f, 3f));

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
                var node = UiBuilder.CreateNode("Tab" + i, _contentArea.parent);
                var rect = node.GetComponent<RectTransform>();
                UiBuilder.Place(rect, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(14f, -TitleBarHeight - 20f - i * TabSpacing),
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

                var label = UiBuilder.CreateText("TabLabel" + i, node.transform,
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

            BuildRows(index);
        }

        // ================= 行构建 =================

        private static void BuildRows(int page)
        {
            foreach (var row in Rows)
                if (row.Rect != null) UnityEngine.Object.Destroy(row.Rect.gameObject);
            Rows.Clear();
            _keyInput = null;

            // 换页回到顶部：否则会停留在上一页的滚动位置，看起来像"内容没了"
            _scrollOffset = 0f;
            _scrollVelocity = 0f;

            var model = BuildPageModel(page);
            var font = UiBuilder.LoadFont(13);
            var contentWidth = WindowWidth - TabColumnWidth - ContentPadding * 2f;

            // 内容高度决定能滚多远。行数多的一页（14 行）会显著超出视口高度。
            _contentHeight = model.Count * RowHeight + ContentBottomPadding;
            if (_contentArea != null)
            {
                _contentArea.sizeDelta = new Vector2(0f, Mathf.Max(_contentHeight, _viewportHeight));
                _contentArea.anchoredPosition = Vector2.zero;
            }

            for (var i = 0; i < model.Count; i++)
            {
                var row = model[i];
                // 从内容区顶部开始排（内容区已锚在视口顶部，滚动靠移动它实现）
                var y = -i * RowHeight;

                var node = UiBuilder.CreateNode("Row" + i, _contentArea);
                var rect = node.GetComponent<RectTransform>();
                UiBuilder.Place(rect, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(0f, y), new Vector2(contentWidth, RowHeight - 6f));

                // 圆角行底板 + 斑马纹
                var bg = node.AddComponent<Image>();
                bg.sprite = AceTheme.Card(RowRadius, 1,
                    i % 2 == 0 ? AceTheme.RowBgA : AceTheme.RowBgB,
                    AceTheme.RowBorder);
                bg.type = Image.Type.Sliced;
                bg.raycastTarget = false;

                // 标题与说明之间留 4px 间隙：说明字号更小、颜色更暗，
                // 靠「字号 + 灰度 + 间距」三重区分层级，不再靠挤在一起
                var label = UiBuilder.CreateText("RowLabel" + i, node.transform,
                    row.Label, font, 13, AceTheme.TextMain, TextAnchor.UpperLeft);
                UiBuilder.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(16f, -10f), new Vector2(460f, 22f));

                var hint = UiBuilder.CreateText("RowHint" + i, node.transform,
                    row.Hint ?? string.Empty, font, 11, AceTheme.TextDim, TextAnchor.UpperLeft);
                UiBuilder.Place(hint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(16f, -36f), new Vector2(contentWidth - 32f, 20f));

                if (row.Kind == RowKind.TextField)
                {
                    // 密钥行：右侧挂一个真正的输入控件。
                    var hostNode = UiBuilder.CreateNode("InputHost", node.transform);
                    var hostRect = hostNode.GetComponent<RectTransform>();
                    UiBuilder.Place(hostRect, new Vector2(1f, 1f), new Vector2(1f, 1f),
                        new Vector2(-16f, -6f), new Vector2(260f, 30f));

                    _keyInput = new TextInputField(hostRect, row.GetText, row.SetText, true, "点这里输入密钥");
                    Rows.Add(new RowEntry { Model = row, Rect = rect, Background = bg });
                    continue;
                }

                if (row.Kind == RowKind.Number)
                {
                    // 数字行使用三个独立区域：减号 / 当前值 / 加号。
                    // 不再通过整行左右半边猜测，避免用户点减号却触发加号。
                    var minus = UiBuilder.CreateNode("Minus", node.transform);
                    var minusRect = minus.GetComponent<RectTransform>();
                    UiBuilder.Place(minusRect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                        new Vector2(-196f, 0f), new Vector2(34f, 30f));
                    var minusBg = minus.AddComponent<Image>();
                    minusBg.sprite = AceTheme.Card(6, 0, AceTheme.BtnMinusBg, AceTheme.BtnMinusBg);
                    minusBg.type = Image.Type.Sliced;
                    minusBg.raycastTarget = false;
                    var minusText = UiBuilder.CreateText("MinusText", minus.transform,
                        "−", font, 18, AceTheme.TextMain, TextAnchor.MiddleCenter, FontStyle.Bold);
                    UiBuilder.Stretch(minusText.rectTransform, 0f, 0f, 0f, 0f);

                    var value = UiBuilder.CreateText("RowValue" + i, node.transform,
                        "", font, 13, AceTheme.Accent, TextAnchor.MiddleCenter, FontStyle.Bold);
                    UiBuilder.Place(value.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                        new Vector2(-136f, 0f), new Vector2(82f, 30f));

                    var plus = UiBuilder.CreateNode("Plus", node.transform);
                    var plusRect = plus.GetComponent<RectTransform>();
                    UiBuilder.Place(plusRect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                        new Vector2(-78f, 0f), new Vector2(34f, 30f));
                    var plusBg = plus.AddComponent<Image>();
                    plusBg.sprite = AceTheme.Card(6, 0, AceTheme.BtnPlusBg, AceTheme.BtnPlusBg);
                    plusBg.type = Image.Type.Sliced;
                    plusBg.raycastTarget = false;
                    var plusText = UiBuilder.CreateText("PlusText", plus.transform,
                        "+", font, 18, AceTheme.TextMain, TextAnchor.MiddleCenter, FontStyle.Bold);
                    UiBuilder.Stretch(plusText.rectTransform, 0f, 0f, 0f, 0f);

                    Rows.Add(new RowEntry
                    {
                        Model = row,
                        Rect = rect,
                        Background = bg,
                        Value = value,
                        MinusRect = minusRect,
                        PlusRect = plusRect,
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
                        new Vector2(-18f, 0f), new Vector2(52f, 26f));

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

                var normalValue = UiBuilder.CreateText("RowValue" + i, node.transform,
                    "", font, 13, AceTheme.Accent, TextAnchor.MiddleRight, FontStyle.Bold);
                UiBuilder.Place(normalValue.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                    new Vector2(-16f, 0f), new Vector2(300f, 30f));
                Rows.Add(new RowEntry
                {
                    Model = row,
                    Rect = rect,
                    Background = bg,
                    Value = normalValue,
                });
            }

            RefreshRowValues();
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
                case 4: return BuildAiPage();
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
            list.Add(Toggle("右上角监控面板", cfg.ShowOverlay,
                "一直显示防护状态和规则命中排行。游戏中按 F8 可以临时关掉。"));
            list.Add(Toggle("顶部弹出提醒", cfg.ShowNotifications,
                "命中检测规则时在屏幕上方弹一条通知。"));
            list.Add(Toggle("开机启动动画", cfg.ShowDesktopSplash,
                "进游戏时在桌面右下角弹一下 Apex Cheat Ender 的加载动画。"));
            list.Add(Toggle("自定义主菜单背景", cfg.ShowMainMenuArt,
                "把主菜单背景换成内置插画。图片已打包进插件，不需要额外文件。"));
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
            list.Add(Toggle("输出详细日志", cfg.VerboseLogging,
                "只在怀疑误判、想查原因时开。日志会长得很快，平时关着。"));
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
        private static List<SettingRow> BuildAboutPage()
        {
            var list = new List<SettingRow>();
            // 版本号直接取常量，避免与 csproj 的 <Version> 各写各的（曾因此对不上）
            list.Add(Info("插件名称", "Apex Cheat Ender",
                () => AntiCheatPlugin.PluginVersion));
            list.Add(Info("快捷键", "Insert 打开这个界面，F8 开关右上角监控面板。", () => ""));
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
                GetValue = () => integer
                    ? "[ - ]  " + entry.Value.ToString("F0") + "  [ + ]"
                    : "[ - ]  " + entry.Value.ToString("F2") + "  [ + ]",
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
                GetValue = () => "[ - ]  " + entry.Value + "  [ + ]",
                OnMinus = () =>
                {
                    entry.Value = Mathf.Clamp(entry.Value - (int)step, (int)min, (int)max);
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
                OnPlus = () =>
                {
                    entry.Value = Mathf.Clamp(entry.Value + (int)step, (int)min, (int)max);
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

        // ================= AI 页 =================

        /// <summary>
        /// AI 页。
        ///
        /// 只有一个供应商，且用户唯一需要提供的东西是一把密钥——
        /// 接口地址、模型名这些全在 <see cref="AiProviders"/> 里写死，
        /// 用户填错一个字符就 404 的机会被彻底消掉。
        ///
        /// 密钥用自制输入控件：候选字符网格点选 + 剪贴板粘贴，
        /// 不依赖 uGUI 的 InputField（它在 IL2CPP 下挂委托不稳）。
        /// </summary>
        private static List<SettingRow> BuildAiPage()
        {
            var cfg = AntiCheatRuntime.Config;
            var list = new List<SettingRow>();
            if (cfg == null) return list;

            var ai = AntiCheatRuntime.AiAnalyzer;
            var provider = AiProviders.Resolve(cfg.AiProvider.Value);

            var problem = ai?.ConfigProblem;
            list.Add(Info("现在的状态",
                string.IsNullOrEmpty(problem) ? "都配好了，AI 可以用了。" : "还差一步才能用。",
                () => string.IsNullOrEmpty(problem) ? "可以用了 ✓" : problem));

            list.Add(Toggle("启用 AI 分析", cfg.AiAnalysisEnabled,
                "让大模型帮忙看一眼规则引擎命中的行为，判断是不是真的作弊。"
                + "它只是多一重参考，最终判定权还在规则引擎手上。默认关着，因为要花钱。"));

            list.Add(Action("用哪家", "换供应商只需要在这里点一下。想加别家，在代码的 AiProviders.cs 里加一行。",
                () =>
                {
                    var all = AiProviders.All;
                    var i = 0;
                    for (var k = 0; k < all.Count; k++)
                        if (all[k].DisplayName == cfg.AiProvider.Value) { i = k; break; }
                    cfg.AiProvider.Value = all[(i + 1) % all.Count].DisplayName;
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                    BuildRows(_currentPage);
                },
                () => provider.DisplayName));

            list.Add(Info("这家怎么样", provider.Summary, () => ""));
            list.Add(Info("密钥长什么样", "形如 " + provider.KeyHint + "。", () => ""));
            list.Add(Info("去哪拿密钥", provider.SignupUrl, () => "[ 见下面说明 ]"));

            list.Add(new SettingRow
            {
                Label = "密钥",
                Hint = "点输入框开始填。键盘上按 Ctrl+V 可以直接粘贴，Backspace 删掉最后一位。",
                Kind = RowKind.TextField,
                GetValue = () => string.IsNullOrEmpty(provider.NormalizeKey(cfg.AiApiKey.Value))
                    ? "还没填"
                    : "已填好 ✓",
                GetText = () => cfg.AiApiKey.Value ?? string.Empty,
                SetText = v =>
                {
                    cfg.AiApiKey.Value = v;
                    FlashSaved();
                    AntiCheatRuntime.ApplyConfigChange();
                },
            });

            list.Add(Number("命中几条规则才去问 AI", cfg.AiMinHitsToTrigger, 1f, 1f, 20f,
                "一条都没命中就不花钱去问了。调高一点更省钱，因为 AI 只做最后确认。", true));
            list.Add(Number("同一个人隔多久再问一次", cfg.AiCooldownSeconds, 5f, 5f, 600f,
                "防止一个人持续作弊导致账单失控。", true));
            list.Add(Number("等它多久算超时", cfg.AiTimeoutSeconds, 1f, 3f, 60f,
                "模型没在这个时间内回答，就放弃本次分析。", true));
            list.Add(Toggle("把玩家昵称一起发过去", cfg.AiSendPlayerNames,
                "关掉的话只发 Player#编号，不发真名。建议保持关闭，行为数据一样能分析。"));

            var verdicts = AntiCheatRuntime.Verdicts;
            if (verdicts == null) return list;

            foreach (var v in verdicts.RankedVerdicts())
            {
                if (v.EvaluateLevel() == RiskLevel.Normal) continue;

                var pid = v.PlayerId;
                var verdict = v;
                list.Add(Action("分析 " + v.Name, "把这个玩家最近的 RPC 序列发给大模型看一遍。",
                    () => AntiCheatRuntime.AiAnalyzer?.ManualAnalyze(pid, verdict),
                    () =>
                    {
                        var st = AntiCheatRuntime.AiAnalyzer?.GetState(pid);
                        if (st == null) return "[ 点击分析 ]";
                        if (st.Running) return "[ 分析中... ]";
                        return "[ " + Shorten(st.Summary, 20) + " ]";
                    }));
            }

            return list;
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        // ================= 交互 =================

        /// <summary>每帧调用。</summary>
        /// <summary>
        /// 设置界面当前是否打开。
        /// 监控面板常驻右上角，正好压在设置窗口的标题栏上 —— 屏幕就那么大，
        /// 两个面板叠在一起谁都看不清。由监控面板读这个标志来让位。
        /// </summary>
        public static bool IsOpen => _visible;

        public static void Tick(bool toggleRequested)
        {
            if (!_built || _root == null) return;

            if (toggleRequested)
            {
                _visible = !_visible;
                _root.SetActive(_visible);
                if (_visible)
                {
                    _nextRowRefresh = 0f;     // 打开时立刻刷一次
                    _scrollVelocity = 0f;
                    _lastMouseY = Input.mousePosition.y;
                }
            }

            if (!_visible) return;

            // 密钥输入控件先吃事件，避免点键盘时顺带把别的开关点了
            var inputCaptured = false;
            if (_keyInput != null)
            {
                _keyInput.Tick();
                inputCaptured = _keyInput.CapturesMouse;
            }

            if (!inputCaptured)
            {
                HandleScroll();
                HandleClick();
            }

            ApplyScroll();

            // 节流到 5Hz：行值里只有 AI 状态是异步变化的，不需要每帧重算。
            // 原先每帧遍历所有行 + 字符串 Contains，是可见期间的持续垃圾来源。
            if (Time.time >= _nextRowRefresh)
            {
                _nextRowRefresh = Time.time + 0.2f;
                RefreshRowValues();
            }

            RefreshFooter();
        }

        // ================= 滚动 =================

        /// <summary>
        /// 处理滚轮与拖拽。只更新偏移量，实际位移统一交给 <see cref="ApplyScroll"/>。
        /// 设置项最多的一页有 14 行（870px），视口只有约 520px，必须能滚。
        /// </summary>
        private static void HandleScroll()
        {
            var max = Mathf.Max(0f, _contentHeight - _viewportHeight);
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
                if (Input.GetMouseButton(0))
                {
                    var dy = mouse.y - _lastMouseY;
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
            var max = Mathf.Max(0f, _contentHeight - _viewportHeight);
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

        private static void HandleClick()
        {
            // 改成「按下记录 + 松开触发」：
            // 若直接用 GetMouseButtonDown，用户想按住拖拽滚动时会顺带把行上的开关点掉。
            if (Input.GetMouseButtonDown(0))
            {
                _pressPos = Input.mousePosition;
                _pressArmed = true;
                return;
            }

            if (!Input.GetMouseButtonUp(0)) return;
            if (!_pressArmed) return;
            _pressArmed = false;

            var mouse = Input.mousePosition;

            // 移动超过阈值 → 判定为拖拽滚动，不触发点击
            if (Mathf.Abs(mouse.x - _pressPos.x) > 6f ||
                Mathf.Abs(mouse.y - _pressPos.y) > 6f) return;

            foreach (var tab in Tabs)
            {
                if (!RectTransformUtility.RectangleContainsScreenPoint(tab.Rect, mouse, null)) continue;
                if (tab.Index != _currentPage) SwitchPage(tab.Index);
                return;
            }

            // 行命中必须先确认鼠标在视口内 —— 被裁剪掉（滚出视口）的行，
            // 其 RectTransform 的屏幕坐标仍在视口之外，不判这一条会点错。
            if (!MouseOverViewport(mouse)) return;

            foreach (var row in Rows)
            {
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
