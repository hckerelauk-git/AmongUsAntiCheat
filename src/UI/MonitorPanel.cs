using System.Collections.Generic;
using ApexCheatEnder.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ApexCheatEnder.UI
{
    /// <summary>
    /// Apex Cheat Ender 监控面板：常驻屏幕右上角，显示防护状态与规则命中排行。
    ///
    /// ────────────── 版式（参考 Amethyst 的视觉语言，配色不变） ──────────────
    ///
    ///   ┌──────────────────────────────────────────────┐
    ///   │ ┌──┐  APEX CHEAT ENDER          ⬤ 防护中      │  ← 图标徽章 + 标题 + 状态胶囊
    ///   │ └──┘                                          │
    ///   │ ──────────────────────────────────────────── │
    ///   │ ┌ 插件 5 ┐┌ 信任 2 ┐┌ 命中 0 ┐               │  ← 三枚统计芯片
    ///   │                                               │
    ///   │  规则命中 ───                                 │  ← 小节标题 + 短强调线
    ///   │  ┌ 高危 ┐ Player_01        3 条  ▰▰▰▰▱▱▱    │  ← 列表行：标签芯片/名字/计数/进度条
    ///   │  ┌ 命中 ┐ Impostor_X       2 条  ▰▰▱▱▱▱▱    │
    ///   │ ──────────────────────────────────────────── │
    ///   │  [F8] 隐藏面板                                │  ← 键帽 + 提示
    ///   └──────────────────────────────────────────────┘
    ///
    /// 相比上一版的三处结构性改动（这才是「AI 味」的来源）：
    ///   1. **去掉上下两条通铺的彩色长条** —— 那是典型「生成式仪表盘」套路，
    ///      换成「头部一条细分隔线 + 左侧对齐的呼吸感留白」
    ///   2. **进度条改成实心圆角条**，不再用 Unicode `█░` 方块字符 ——
    ///      方块字符在不同字体下宽窄不一，行与行永远对不齐
    ///   3. **状态与标签改用胶囊芯片承载**，不再用 `●` 加纯文本；
    ///      列表行做列对齐（标签 / 名字 / 计数 / 条），四列各自成线
    ///
    /// 按 F8 切换显示。
    /// </summary>
    internal static class MonitorPanel
    {
        // ================= 面板尺寸 =================
        private const float PanelWidth = 360f;
        private const float PanelHeight = 290f;
        private const float Margin = 20f;
        private const float PadX = 14f;

        // ================= 头部 =================
        private const float HeaderTop = 12f;
        private const float BadgeSize = 30f;
        private const float TitleLeft = 52f;
        private const float StatusChipW = 96f;
        private const float StatusChipH = 22f;

        // ================= 纵向节奏（距面板顶部） =================
        private const float Divider1Y = 50f;
        private const float ChipsTop = 58f;
        private const float ChipHeight = 22f;
        private const float ChipGap = 6f;
        private const float SectionTop = 90f;
        private const float ListTop = 106f;
        private const float RowHeight = 24f;
        private const float Divider2Y = 256f;
        private const float FooterTop = 260f;

        // ================= 列表行内部列宽 =================
        private const float MarkerW = 44f;
        private const float MarkerH = 18f;
        private const float NameLeft = 64f;
        private const float NameW = 150f;
        private const float CountLeft = 214f;
        private const float CountW = 56f;
        private const float BarLeft = 278f;
        private const float BarW = 68f;
        private const float BarH = 6f;

        private const int MaxRows = 6;
        private const int CardRadius = 14;
        private const int MarkerRadius = 9;

        /// <summary>面板刷新间隔（秒）。</summary>
        private const float RefreshInterval = 0.2f;

        /// <summary>延迟显示时长（秒）——等反作弊初始化完成再出现。</summary>
        private const float ShowDelaySeconds = 4.6f;

        // ================= 状态 =================
        private static bool _built;
        private static GameObject _root;
        private static RectTransform _rect;

        private static Image _badge;
        private static Image _shield;
        private static Text _headerText;

        private static Image _statusChip;
        private static Image _statusDot;
        private static Text _statusText;

        private static Text _statPlugins;
        private static Text _statTrusted;
        private static Text _statHits;

        private static Text _emptyText;

        private static bool _visible = true;
        private static bool _visibleInitialized;
        private static float _firstTickTime = -1f;
        private static float _nextRefreshTime;

        private static readonly List<Row> Rows = new List<Row>(MaxRows);
        private static readonly List<PlayerVerdict> FlaggedBuffer = new List<PlayerVerdict>(16);

        /// <summary>列表行的一组控件引用。</summary>
        private sealed class Row
        {
            public GameObject Root;
            public Image MarkerBg;
            public Text MarkerText;
            public Text Name;
            public Text Count;
            public Image BarFill;
        }

        // ================= 构建 =================

        public static void EnsureBuilt(Transform canvasRoot)
        {
            if (_built || canvasRoot == null) return;
            _built = true;

            var font = UiBuilder.LoadFont(12);

            _root = UiBuilder.CreateNode("AceMonitor", canvasRoot);
            _rect = _root.GetComponent<RectTransform>();
            UiBuilder.Place(_rect,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-Margin, -Margin),
                new Vector2(PanelWidth, PanelHeight));

            // ---- 底板：圆角卡片 ----
            var bg = UiBuilder.CreateImage("Bg", _root.transform, Color.white);
            bg.sprite = AceTheme.Card(CardRadius, 1, AceTheme.PanelBg, AceTheme.Border);
            bg.type = Image.Type.Sliced;
            UiBuilder.Stretch(bg.rectTransform, 0f, 0f, 0f, 0f);

            BuildHeader(font);
            BuildDivider("Divider1", Divider1Y);
            BuildStatChips(font);
            BuildSectionLabel(font);
            BuildRows(font);
            BuildDivider("Divider2", Divider2Y);
            BuildFooter(font);

            // ---- 空态提示：居中在列表区 ----
            _emptyText = UiBuilder.CreateText("Empty", _root.transform,
                "当前无人命中规则", font, 11, AceTheme.TextDim,
                TextAnchor.MiddleCenter);
            UiBuilder.Place(_emptyText.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(PadX, -ListTop),
                new Vector2(PanelWidth - PadX * 2f, RowHeight * 2f));
        }

        /// <summary>头部：图标徽章 + 标题 + 状态胶囊。</summary>
        private static void BuildHeader(Font font)
        {
            // 图标徽章：圆角方块 + 粗描边（Amethyst 的签名元素）
            _badge = UiBuilder.CreateImage("Badge", _root.transform, Color.white);
            _badge.sprite = AceTheme.Badge((int)BadgeSize, AceTheme.Primary);
            _badge.type = Image.Type.Sliced;
            UiBuilder.Place(_badge.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(PadX, -HeaderTop),
                new Vector2(BadgeSize, BadgeSize));

            // 盾牌徽标压在徽章正中
            var shieldTex = AceTheme.MakeShield(40, AceTheme.Primary, AceTheme.Accent);
            _shield = UiBuilder.CreateImage("Shield", _root.transform, Color.white, shieldTex);
            UiBuilder.Place(_shield.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(PadX + 7f, -(HeaderTop + 7f)),
                new Vector2(16f, 16f));

            // 标题
            _headerText = UiBuilder.CreateText("Header", _root.transform,
                "ACE", font, 13, AceTheme.TextMain,
                TextAnchor.MiddleLeft, FontStyle.Bold);
            UiBuilder.Place(_headerText.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(TitleLeft, -HeaderTop),
                new Vector2(PanelWidth - TitleLeft - StatusChipW - PadX - 10f, BadgeSize));

            // 状态胶囊：底色芯片 + 圆点 + 文字，比「● 防护中」更像成品控件
            _statusChip = UiBuilder.CreateImage("StatusChip", _root.transform, Color.white);
            _statusChip.sprite = AceTheme.Chip(StatusChipH, AceTheme.Success);
            _statusChip.type = Image.Type.Sliced;
            UiBuilder.Place(_statusChip.rectTransform,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-PadX, -(HeaderTop + (BadgeSize - StatusChipH) * 0.5f)),
                new Vector2(StatusChipW, StatusChipH));

            _statusDot = UiBuilder.CreateImage("Dot", _statusChip.transform, AceTheme.Success);
            _statusDot.sprite = AceTheme.Dot(7, AceTheme.Success);
            UiBuilder.Place(_statusDot.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(9f, 0f), new Vector2(7f, 7f));

            _statusText = UiBuilder.CreateText("Text", _statusChip.transform,
                "防护中", font, 11, AceTheme.Success,
                TextAnchor.MiddleLeft, FontStyle.Bold);
            UiBuilder.Place(_statusText.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(21f, 0f), new Vector2(StatusChipW - 25f, StatusChipH));
        }

        /// <summary>三枚统计芯片：插件 / 信任 / 命中。</summary>
        private static void BuildStatChips(Font font)
        {
            var w = (PanelWidth - PadX * 2f - ChipGap * 2f) / 3f;

            _statPlugins = BuildStatChip("StatPlugins", font, 0, w);
            _statTrusted = BuildStatChip("StatTrusted", font, 1, w);
            _statHits = BuildStatChip("StatHits", font, 2, w);
        }

        private static Text BuildStatChip(string name, Font font, int index, float width)
        {
            var chip = UiBuilder.CreateImage(name, _root.transform, Color.white);
            chip.sprite = AceTheme.Chip(ChipHeight, AceTheme.Border);
            chip.type = Image.Type.Sliced;
            UiBuilder.Place(chip.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(PadX + index * (width + ChipGap), -ChipsTop),
                new Vector2(width, ChipHeight));

            var text = UiBuilder.CreateText("Text", chip.transform,
                "--", font, 11, AceTheme.TextMain, TextAnchor.MiddleCenter);
            UiBuilder.Stretch(text.rectTransform, 2f, 0f, 2f, 0f);
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        /// <summary>小节标题 + 短强调线（Amethyst 用短横线做分组标记，比整条分隔线轻）。</summary>
        private static void BuildSectionLabel(Font font)
        {
            var label = UiBuilder.CreateText("SectionLabel", _root.transform,
                "规则命中", font, 11, AceTheme.Accent,
                TextAnchor.MiddleLeft, FontStyle.Bold);
            UiBuilder.Place(label.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(PadX, -SectionTop),
                new Vector2(80f, 16f));

            var rule = UiBuilder.CreateImage("SectionRule", _root.transform, Color.white);
            rule.sprite = AceTheme.Card(1, 0, AceTheme.Border, AceTheme.Border);
            rule.type = Image.Type.Sliced;
            UiBuilder.Place(rule.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(PadX + 68f, -(SectionTop + 7f)),
                new Vector2(PanelWidth - PadX * 2f - 68f, 1f));
        }

        private static void BuildRows(Font font)
        {
            for (var i = 0; i < MaxRows; i++)
            {
                var y = ListTop + i * RowHeight;

                var rowGo = UiBuilder.CreateNode($"Row{i}", _root.transform);
                var rowRect = rowGo.GetComponent<RectTransform>();
                UiBuilder.Place(rowRect,
                    new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(0f, -y),
                    new Vector2(PanelWidth, RowHeight));

                // 风险标签芯片
                var markerBg = UiBuilder.CreateImage("Marker", rowGo.transform, Color.white);
                markerBg.sprite = AceTheme.Chip(MarkerH, AceTheme.Warning);
                markerBg.type = Image.Type.Sliced;
                UiBuilder.Place(markerBg.rectTransform,
                    new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(PadX, -(RowHeight - MarkerH) * 0.5f),
                    new Vector2(MarkerW, MarkerH));

                var markerText = UiBuilder.CreateText("MarkerText", markerBg.transform,
                    "", font, 10, AceTheme.Warning, TextAnchor.MiddleCenter, FontStyle.Bold);
                UiBuilder.Stretch(markerText.rectTransform, 0f, 0f, 0f, 0f);

                // 玩家名
                var nameText = UiBuilder.CreateText("Name", rowGo.transform,
                    "", font, 11, AceTheme.TextMain, TextAnchor.MiddleLeft);
                UiBuilder.Place(nameText.rectTransform,
                    new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(NameLeft, 0f),
                    new Vector2(NameW, RowHeight));
                nameText.horizontalOverflow = HorizontalWrapMode.Wrap;
                nameText.verticalOverflow = VerticalWrapMode.Truncate;
                nameText.supportRichText = false;

                // 命中条数（右对齐，与下面的进度条形成固定列）
                var countText = UiBuilder.CreateText("Count", rowGo.transform,
                    "", font, 11, AceTheme.TextDim, TextAnchor.MiddleRight);
                UiBuilder.Place(countText.rectTransform,
                    new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(CountLeft, 0f),
                    new Vector2(CountW, RowHeight));

                // 进度条：轨道 + 填充（实心圆角，替代 █░）
                var track = UiBuilder.CreateImage("BarTrack", rowGo.transform, Color.white);
                track.sprite = AceTheme.Bar(BarH, AceTheme.Track);
                track.type = Image.Type.Sliced;
                UiBuilder.Place(track.rectTransform,
                    new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(BarLeft, -(RowHeight - BarH) * 0.5f),
                    new Vector2(BarW, BarH));

                var fill = UiBuilder.CreateImage("BarFill", track.transform, Color.white);
                fill.sprite = AceTheme.Bar(BarH, AceTheme.Warning);
                fill.type = Image.Type.Sliced;
                UiBuilder.Place(fill.rectTransform,
                    new Vector2(0f, 0f), new Vector2(0f, 0f),
                    Vector2.zero,
                    new Vector2(BarW, BarH));

                Rows.Add(new Row
                {
                    Root = rowGo,
                    MarkerBg = markerBg,
                    MarkerText = markerText,
                    Name = nameText,
                    Count = countText,
                    BarFill = fill,
                });

                rowGo.SetActive(false);
            }
        }

        private static void BuildDivider(string name, float y)
        {
            var divider = UiBuilder.CreateImage(name, _root.transform, Color.white);
            divider.sprite = AceTheme.Card(1, 0, AceTheme.Border, AceTheme.Border);
            divider.type = Image.Type.Sliced;
            UiBuilder.Place(divider.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(PadX, -y),
                new Vector2(PanelWidth - PadX * 2f, 1f));
        }

        /// <summary>底部：键帽 + 提示（Amethyst 用键帽而不是纯文本写快捷键）。</summary>
        private static void BuildFooter(Font font)
        {
            var capTex = AceTheme.KeyCap(26, 18, AceTheme.BadgeBg, AceTheme.Border, AceTheme.Border);
            var cap = UiBuilder.CreateImage("KeyCap", _root.transform, Color.white, capTex);
            UiBuilder.Place(cap.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(PadX, -FooterTop),
                new Vector2(26f, 18f));

            var capText = UiBuilder.CreateText("KeyCapText", cap.transform,
                "F8", font, 10, AceTheme.TextMain, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiBuilder.Place(capText.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, -1f), new Vector2(26f, 15f));

            var hint = UiBuilder.CreateText("Hint", _root.transform,
                "隐藏面板", font, 10, AceTheme.TextDim, TextAnchor.MiddleLeft);
            UiBuilder.Place(hint.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(PadX + 32f, -FooterTop),
                new Vector2(160f, 18f));
        }

        // ================= 每帧刷新 =================

        public static void Tick()
        {
            if (!_built || _root == null) return;

            if (_firstTickTime < 0f) _firstTickTime = Time.time;

            if (!_visibleInitialized)
            {
                _visibleInitialized = true;
                _visible = AntiCheatRuntime.Config?.ShowOverlay.Value ?? true;
            }

            // 等反作弊初始化完成后再出现
            var ready = AntiCheatRuntime.IsReady && (Time.time - _firstTickTime >= ShowDelaySeconds);

            // 设置窗口打开时让位：它占着屏幕中央，而本面板常驻右上角，
            // 两者会叠在标题栏上互相遮挡。
            var shouldShow = ready && _visible && !SettingsWindow.IsOpen;
            if (_root.activeSelf != shouldShow) _root.SetActive(shouldShow);
            if (!shouldShow) return;

            // 节流：不到刷新时刻直接返回，不重算、不拼串、不赋值。
            var now = Time.time;
            if (now < _nextRefreshTime) return;
            _nextRefreshTime = now + RefreshInterval;

            try { Refresh(); }
            catch { /* 刷新失败不影响游戏 */ }
        }

        /// <summary>切换显示。由 F8 触发。</summary>
        public static void Toggle()
        {
            _visible = !_visible;
            _visibleInitialized = true;
            if (_root != null) _root.SetActive(_visible);
        }

        private static void Refresh()
        {
            var verdicts = AntiCheatRuntime.Verdicts;
            if (verdicts == null) return;

            // ---- 统计芯片 ----
            var report = AntiCheatRuntime.ScanReport;
            var pluginCount = report?.AllPlugins.Count ?? 0;
            var trustedCount = report?.TrustedPlugins.Count ?? 0;
            var suspiciousCount = report?.Violations.Count ?? 0;

            _statPlugins.text = $"插件 {pluginCount}";
            _statTrusted.text = $"信任 {trustedCount}";
            _statHits.text = $"命中 {suspiciousCount}";

            // ---- 依据「规则命中」而不是分数来筛选与标记 ----
            var ranked = verdicts.RankedVerdicts();

            var flagged = FlaggedBuffer;
            flagged.Clear();
            var maxLevel = RiskLevel.Normal;

            foreach (var v in ranked)
            {
                var level = v.EvaluateLevel();
                if (level > maxLevel) maxLevel = level;
                if (level != RiskLevel.Normal) flagged.Add(v);
            }

            if (flagged.Count == 0)
            {
                foreach (var r in Rows) r.Root.SetActive(false);
                _emptyText.gameObject.SetActive(true);
                SetGlobalStatus(RiskLevel.Normal);
                return;
            }

            _emptyText.gameObject.SetActive(false);

            // 按风险等级降序，同级按命中条数降序
            flagged.Sort((a, b) =>
            {
                var la = a.EvaluateLevel();
                var lb = b.EvaluateLevel();
                return la != lb ? lb.CompareTo(la) : b.EvidenceCount.CompareTo(a.EvidenceCount);
            });

            // 进度条以命中条数为满格基准
            var scale = 1;
            foreach (var v in flagged)
                if (v.EvidenceCount > scale) scale = v.EvidenceCount;

            for (var i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                if (i >= flagged.Count)
                {
                    row.Root.SetActive(false);
                    continue;
                }

                var v = flagged[i];
                var level = v.EvaluateLevel();
                var color = ColorOf(level);

                row.Root.SetActive(true);

                // 标签芯片：底色不变，只换描边色与文字色，避免引入新配色
                row.MarkerBg.sprite = AceTheme.Chip(MarkerH, color);
                row.MarkerText.text = MarkerOf(level);
                row.MarkerText.color = color;

                row.Name.text = v.Name;
                row.Count.text = $"{v.EvidenceCount} 条";

                // 进度条：按命中条数占满格比例
                var ratio = Mathf.Clamp01(v.EvidenceCount / (float)scale);
                var w = Mathf.Max(BarH, BarW * ratio);
                row.BarFill.sprite = AceTheme.Bar(BarH, color);
                var fr = row.BarFill.rectTransform;
                fr.sizeDelta = new Vector2(w, BarH);
                fr.anchoredPosition = Vector2.zero;
            }

            SetGlobalStatus(maxLevel);
        }

        /// <summary>各风险等级对应的语义色（全部取自 AceTheme，不新增颜色）。</summary>
        private static Color ColorOf(RiskLevel level) => level switch
        {
            RiskLevel.Confirmed  => AceTheme.Danger,
            RiskLevel.HighRisk   => AceTheme.Danger,
            RiskLevel.Suspicious => AceTheme.Warning,
            _                    => AceTheme.Success,
        };

        /// <summary>各风险等级在列表里显示的标签文字。</summary>
        private static string MarkerOf(RiskLevel level) => level switch
        {
            RiskLevel.Confirmed  => "已确认",
            RiskLevel.HighRisk   => "高危",
            RiskLevel.Suspicious => "命中",
            _                    => "正常",
        };

        /// <summary>按判定结果切换头部状态胶囊与徽章配色。</summary>
        private static void SetGlobalStatus(RiskLevel level)
        {
            var color = ColorOf(level);
            var text = level switch
            {
                RiskLevel.Confirmed  => "已确认作弊",
                RiskLevel.HighRisk   => "高危",
                RiskLevel.Suspicious => "规则命中",
                _                    => "防护中",
            };

            if (_statusChip != null)
                _statusChip.sprite = AceTheme.Chip(StatusChipH, color);

            if (_statusDot != null)
            {
                _statusDot.sprite = AceTheme.Dot(7, color);
                _statusDot.color = color;
            }

            if (_statusText != null)
            {
                _statusText.text = text;
                _statusText.color = color;
            }

            // 徽章描边随状态变色，头部一眼能看出「现在是安全还是告警」
            if (_badge != null)
                _badge.sprite = AceTheme.Badge((int)BadgeSize, color);

            if (_shield != null)
                _shield.color = level >= RiskLevel.Suspicious ? color : Color.white;
        }
    }
}
