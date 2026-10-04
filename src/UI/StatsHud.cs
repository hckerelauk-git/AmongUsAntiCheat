using ApexCheatEnder.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ApexCheatEnder.UI
{
    /// <summary>
    /// 左上角状态条：帧率 / 延迟 / 房主身份。
    ///
    /// ────────────── 视觉与交互约定 ──────────────
    ///
    /// 沿用本插件统一的版式语言（圆角卡片 + 胶囊芯片 + 固定列），配色全部取自
    /// <see cref="AceTheme"/>，不引入新颜色。
    ///
    /// 位置放左上角：右上角曾是监控面板的位置，而设置窗口居中，
    /// 三者互不遮挡。所有 Graphic 的 raycastTarget 均为 false（由 UiBuilder 保证），
    /// 不会吞掉游戏的鼠标点击。
    ///
    /// 数据来源全部是游戏自己维护的计数，不额外增加网络请求：
    ///   · 帧率 —— <see cref="AntiCheatRuntime.CurrentFps"/>（帧驱动里统计）
    ///   · 延迟 —— <c>InnerNetClient.Ping</c>
    ///   · 房主 —— <c>InnerNetClient.AmHost</c>
    ///
    /// 未联机时不显示这一条：脱离房间谈延迟没有意义，
    /// 留着只会让人以为「延迟 0」是网络很好。
    /// </summary>
    internal static class StatsHud
    {
        private const float Margin = 20f;
        private const float PadX = 12f;
        private const float Height = 30f;
        private const float Gap = 6f;

        private const float FpsWidth = 84f;
        private const float PingWidth = 104f;
        private const float HostWidth = 76f;

        /// <summary>数值刷新间隔（秒）。帧率抖得厉害，太快反而看不清。</summary>
        private const float RefreshInterval = 0.25f;

        private static GameObject _root;
        private static Image _card;
        private static Text _fpsText;
        private static Text _pingText;
        private static Text _hostText;
        private static Image _hostChip;
        private static float _nextRefresh;

        public static void EnsureBuilt(Transform canvasRoot)
        {
            if (canvasRoot == null) return;

            // 用「对象是否还活着」当守卫，而不是一次性布尔标志。
            // 画布万一被销毁重建，一次性标志会让状态条永远不再出现；
            // Unity 的伪空判断能自动识别被销毁的对象，这里就能自愈。
            if (_root != null) return;

            var font = UiBuilder.LoadFont(12);
            var width = PadX * 2f + FpsWidth + Gap + PingWidth + Gap + HostWidth;

            _root = UiBuilder.CreateNode("AceStatsHud", canvasRoot);
            var rect = _root.GetComponent<RectTransform>();
            // 底部居中：左上角会压住任务列表，右上角会跟设置窗口打架，
            // 底部中间是游戏 HUD 唯一长期空着的位置。
            UiBuilder.Place(rect,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, Margin),
                new Vector2(width, Height));

            // 底板：与设置窗口、监控面板同一套圆角卡片
            _card = UiBuilder.CreateImage("Bg", _root.transform, Color.white);
            _card.sprite = AceTheme.Card(9, 1, AceTheme.PanelBg, AceTheme.Border);
            _card.type = Image.Type.Sliced;
            UiBuilder.Stretch(_card.rectTransform, 0f, 0f, 0f, 0f);

            _fpsText = MakeText("Fps", font, PadX, FpsWidth);
            _pingText = MakeText("Ping", font, PadX + FpsWidth + Gap, PingWidth);

            // 房主状态用胶囊芯片强调：它是三者里唯一「影响权限」的信息
            var hostLeft = PadX + FpsWidth + Gap + PingWidth + Gap;
            _hostChip = UiBuilder.CreateImage("HostChip", _root.transform, Color.white);
            _hostChip.sprite = AceTheme.Chip(Height - 12f, AceTheme.Success);
            _hostChip.type = Image.Type.Sliced;
            UiBuilder.Place(_hostChip.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(hostLeft, 0f), new Vector2(HostWidth, Height - 12f));

            _hostText = UiBuilder.CreateText("HostText", _hostChip.transform,
                "--", font, 11, AceTheme.Success, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiBuilder.Stretch(_hostText.rectTransform, 0f, 0f, 0f, 0f);
        }

        private static Text MakeText(string name, Font font, float left, float width)
        {
            var text = UiBuilder.CreateText(name, _root.transform,
                "--", font, 12, AceTheme.TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            UiBuilder.Place(text.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(left, 0f), new Vector2(width, Height));
            return text;
        }

        public static void Tick()
        {
            if (_root == null) return;

            if (!(AntiCheatRuntime.Config?.ShowStatsHud.Value ?? true))
            {
                if (_root.activeSelf) _root.SetActive(false);
                return;
            }

            // 只有真正连上房间才有延迟可言。
            var connected = GameBridge.IsConnected;
            if (_root.activeSelf != connected) _root.SetActive(connected);
            if (!connected) return;

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;

            try
            {
                var fps = AntiCheatRuntime.CurrentFps;
                _fpsText.text = "帧率 " + Mathf.RoundToInt(fps);

                var ping = GameBridge.GetPing();
                _pingText.text = ping < 0 ? "延迟 --" : "延迟 " + ping + " ms";
                // 三档配色，与 Amethyst 的延迟显示一致：
                //   < 80  绿 —— 流畅
                //   < 160 黄 —— 偏高
                //   否则  红 —— 明显卡
                _pingText.color = ping < 0 ? AceTheme.TextDim
                    : ping < 80 ? AceTheme.Success
                    : ping < 160 ? AceTheme.Warning
                    : AceTheme.Danger;

                var isHost = GameBridge.IsHost;
                _hostText.text = isHost ? "房主" : "客户端";
                _hostText.color = isHost ? AceTheme.Success : AceTheme.TextDim;
                _hostChip.sprite = AceTheme.Chip(Height - 12f, isHost ? AceTheme.Success : AceTheme.Border);
            }
            catch
            {
                // 状态条出问题绝不能影响游戏，静默跳过这一帧
            }
        }
    }
}
