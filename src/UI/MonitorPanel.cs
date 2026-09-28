using System.Collections.Generic;
using System.Text;
using AmongUsAntiCheat.Core;
using UnityEngine;
using UnityEngine.UI;

namespace AmongUsAntiCheat.UI
{
    /// <summary>
    /// ACE 监控面板：常驻屏幕右上角，显示防护状态与规则命中排行。
    ///
    /// 布局：
    ///   ┌──────────────────────────────┐
    ///   │ [盾] APEX CHEAT ENDER     ●  │
    ///   │ ──────────────────────────── │
    ///   │ 防护中    插件 5    命中 0    │
    ///   │ 规则命中                    │
    ///   │ Player1  2.40 ██████░░  5条  │
    ///   │ ──────────────────────────── │
    ///   │ F8 隐藏                       │
    ///   └──────────────────────────────┘
    ///
    /// 按 F8 切换显示。
    /// </summary>
    internal static class MonitorPanel
    {
        private const float PanelWidth = 348f;
        private const float PanelHeight = 216f;
        private const float Margin = 20f;
        private const int MaxRows = 6;
        private const int BarLength = 8;

        private static bool _built;
        private static GameObject _root;
        private static Image _shield;
        private static Image _statusBar;
        private static Text _headerText;
        private static Text _statusDot;
        private static Text _summaryText;
        private static Text _listText;
        private static bool _visible = true;
        private static bool _visibleInitialized;

        /// <summary>首次 Tick 的时刻，用于延迟显示。</summary>
        private static float _firstTickTime = -1f;

        /// <summary>
        /// 延迟显示时长（秒）。
        /// 反作弊的初始化是异步的（特征库加载、静态扫描），
        /// 面板应当在初始化完成之后再出现，而不是一进游戏就空着占屏幕。
        /// 这里取略长于桌面启动动画的时长。
        /// </summary>
        private const float ShowDelaySeconds = 4.6f;

        public static void EnsureBuilt(Transform canvasRoot)
        {
            if (_built || canvasRoot == null) return;
            _built = true;

            var font = UiBuilder.LoadFont(12);

            _root = UiBuilder.CreateNode("AceMonitor", canvasRoot);
            var rect = _root.GetComponent<RectTransform>();
            UiBuilder.Place(rect,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-Margin, -Margin),
                new Vector2(PanelWidth, PanelHeight));

            // ---- 底板 ----
            var bg = UiBuilder.CreateImage("Bg", _root.transform, AceTheme.PanelBg);
            UiBuilder.Stretch(bg.rectTransform, 0f, 0f, 0f, 0f);

            // ---- 顶部强调条 ----
            var topBar = UiBuilder.CreateImage("TopBar", _root.transform, AceTheme.Primary);
            UiBuilder.Place(topBar.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                Vector2.zero, new Vector2(PanelWidth, 2f));

            // ---- 盾牌图标 ----
            var shieldTex = AceTheme.MakeShield(48, AceTheme.Primary, AceTheme.Accent);
            _shield = UiBuilder.CreateImage("Shield", _root.transform, Color.white, shieldTex);
            UiBuilder.Place(_shield.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(12f, -11f), new Vector2(24f, 24f));

            // ---- 标题 ----
            _headerText = UiBuilder.CreateText("Header", _root.transform,
                "APEX CHEAT ENDER", font, 13, AceTheme.TextMain,
                TextAnchor.UpperLeft, FontStyle.Bold);
            UiBuilder.Place(_headerText.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(44f, -12f), new Vector2(220f, 20f));

            // ---- 状态点 ----
            _statusDot = UiBuilder.CreateText("StatusDot", _root.transform,
                "● 防护中", font, 11, AceTheme.Success,
                TextAnchor.UpperRight, FontStyle.Bold);
            UiBuilder.Place(_statusDot.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(PanelWidth - 138f, -13f), new Vector2(126f, 18f));

            // ---- 分隔线 ----
            var divider1 = UiBuilder.CreateImage("Divider1", _root.transform, AceTheme.Border);
            UiBuilder.Place(divider1.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(12f, -40f), new Vector2(PanelWidth - 24f, 1f));

            // ---- 摘要行 ----
            _summaryText = UiBuilder.CreateText("Summary", _root.transform,
                "插件 --  信任 --  命中 --", font, 11, AceTheme.TextDim);
            UiBuilder.Place(_summaryText.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(14f, -50f), new Vector2(PanelWidth - 28f, 18f));

            // ---- 排行标题 ----
            var listTitle = UiBuilder.CreateText("ListTitle", _root.transform,
                "规则命中", font, 11, AceTheme.Accent, TextAnchor.UpperLeft, FontStyle.Bold);
            UiBuilder.Place(listTitle.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(14f, -72f), new Vector2(200f, 18f));

            // ---- 排行内容 ----
            _listText = UiBuilder.CreateText("List", _root.transform,
                "当前无人命中规则", font, 11, AceTheme.TextMain);
            UiBuilder.Place(_listText.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(14f, -92f), new Vector2(PanelWidth - 28f, 100f));

            // ---- 底部分隔线 ----
            var divider2 = UiBuilder.CreateImage("Divider2", _root.transform, AceTheme.Border);
            UiBuilder.Place(divider2.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(12f, -196f), new Vector2(PanelWidth - 24f, 1f));

            // ---- 底部提示 ----
            var hint = UiBuilder.CreateText("Hint", _root.transform,
                "F8 隐藏面板", font, 10, AceTheme.TextDim, TextAnchor.UpperLeft);
            UiBuilder.Place(hint.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(14f, -200f), new Vector2(200f, 16f));

            // ---- 底部状态条（随风险变色） ----
            _statusBar = UiBuilder.CreateImage("StatusBar", _root.transform, AceTheme.Success);
            UiBuilder.Place(_statusBar.rectTransform,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                Vector2.zero, new Vector2(PanelWidth, 2f));
        }

        /// <summary>每帧刷新。</summary>
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

            var shouldShow = ready && _visible;
            if (_root.activeSelf != shouldShow) _root.SetActive(shouldShow);
            if (!shouldShow) return;

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

            // ---- 摘要 ----
            var report = AntiCheatRuntime.ScanReport;
            var pluginCount = report?.AllPlugins.Count ?? 0;
            var trustedCount = report?.TrustedPlugins.Count ?? 0;
            var suspiciousCount = report?.Violations.Count ?? 0;

            _summaryText.text = $"插件 {pluginCount}   信任 {trustedCount}   命中 {suspiciousCount}";

            // ---- 依据「规则命中」而不是分数来筛选与标记 ----
            // RankedVerdicts 返回所有被追踪过的玩家（含零证据的），
            // 必须先用 EvaluateLevel 做一次判定，只有判定成立的才进入列表。
            var cfg = AntiCheatRuntime.Config;
            var ranked = verdicts.RankedVerdicts();

            var flagged = new List<PlayerVerdict>();
            var maxLevel = RiskLevel.Normal;

            foreach (var v in ranked)
            {
                var level = v.EvaluateLevel(cfg);
                if (level > maxLevel) maxLevel = level;
                if (level != RiskLevel.Normal) flagged.Add(v);
            }

            if (flagged.Count == 0)
            {
                _listText.text = "当前无人命中规则";
                SetGlobalStatus(RiskLevel.Normal);
                return;
            }

            // 按风险等级降序，同级按命中条数降序
            flagged.Sort((a, b) =>
            {
                var la = a.EvaluateLevel(cfg);
                var lb = b.EvaluateLevel(cfg);
                return la != lb ? lb.CompareTo(la) : b.EvidenceCount.CompareTo(a.EvidenceCount);
            });

            // 条形图以命中条数为满格基准
            var scale = 1f;
            foreach (var v in flagged)
                if (v.EvidenceCount > scale) scale = v.EvidenceCount;

            var sb = new StringBuilder(256);
            var rows = Mathf.Min(flagged.Count, MaxRows);
            for (var i = 0; i < rows; i++)
            {
                var v = flagged[i];
                var level = v.EvaluateLevel(cfg);

                sb.Append(MarkerOf(level)).Append(' ')
                  .Append(v.Name).Append("  ")
                  .Append("命中 ").Append(v.EvidenceCount).Append(" 条  ")
                  .Append(BuildBar(v.EvidenceCount / scale));

                // AI 确认过的额外展示结论摘要
                if (level == RiskLevel.Confirmed && !string.IsNullOrEmpty(v.AiSummary))
                {
                    sb.AppendLine().Append("    AI: ").Append(v.AiSummary);
                }

                if (i < rows - 1) sb.AppendLine();
            }

            _listText.text = sb.ToString();
            SetGlobalStatus(maxLevel);
        }

        /// <summary>各风险等级在列表里显示的标记文字。</summary>
        private static string MarkerOf(RiskLevel level) => level switch
        {
            RiskLevel.Confirmed  => "[已确认]",
            RiskLevel.HighRisk   => "[高危]",
            RiskLevel.Suspicious => "[命中]",
            _                    => "[正常]",
        };

        /// <summary>用 Unicode 方块拼一个横向条形图。</summary>
        private static string BuildBar(float ratio)
        {
            var filled = Mathf.Clamp(Mathf.RoundToInt(ratio * BarLength), 0, BarLength);
            var sb = new StringBuilder(BarLength);
            for (var i = 0; i < BarLength; i++) sb.Append(i < filled ? '█' : '░');
            return sb.ToString();
        }

        /// <summary>
        /// 按**判定结果**切换整体状态展示。
        /// 入参是 RiskLevel——标记必须建立在规则命中之上。
        /// </summary>
        private static void SetGlobalStatus(RiskLevel level)
        {
            Color color;
            string text;

            switch (level)
            {
                case RiskLevel.Confirmed:
                    color = AceTheme.Danger;
                    text = "● 已确认作弊";
                    break;
                case RiskLevel.HighRisk:
                    color = AceTheme.Danger;
                    text = "● 高危";
                    break;
                case RiskLevel.Suspicious:
                    color = AceTheme.Warning;
                    text = "● 规则命中";
                    break;
                default:
                    color = AceTheme.Success;
                    text = "● 防护中";
                    break;
            }

            if (_statusDot != null)
            {
                _statusDot.text = text;
                _statusDot.color = color;
            }

            if (_statusBar != null) _statusBar.color = color;
            if (_shield != null) _shield.color = level >= RiskLevel.Suspicious ? color : Color.white;
        }
    }
}
