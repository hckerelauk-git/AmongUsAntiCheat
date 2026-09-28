using System.Collections.Generic;
using AmongUsAntiCheat.Core;
using UnityEngine;
using UnityEngine.UI;

namespace AmongUsAntiCheat.UI
{
    /// <summary>
    /// 右下角通知卡片。
    ///
    /// 用途：检测到作弊行为时弹出提示，告知「谁、因为什么」被判定。
    ///
    /// 实现要点：
    ///   - 预创建固定数量的卡槽（默认 3 个），复用而不是反复 Instantiate，
    ///     避免在对局中产生 GC 峰值
    ///   - 从屏幕右下角向上堆叠，新通知在最上方
    ///   - 到期自动隐藏；新通知进来时，最老的一条会被顶掉
    /// </summary>
    internal static class NotificationPanel
    {
        private const int SlotCount = 3;
        private const float CardWidth = 348f;
        private const float CardHeight = 70f;
        private const float Margin = 18f;
        private const float Gap = 8f;

        private sealed class Slot
        {
            public GameObject Root;
            public RectTransform Rect;
            public Image Background;
            public Image AccentBar;
            public Text Title;
            public Text Message;
            public float ExpireTime;
            public bool InUse;
        }

        private static readonly List<Slot> Slots = new List<Slot>(SlotCount);
        private static bool _built;

        public static void EnsureBuilt(Transform canvasRoot)
        {
            if (_built || canvasRoot == null) return;
            _built = true;

            var font = UiBuilder.LoadFont(13);

            for (var i = 0; i < SlotCount; i++)
                Slots.Add(BuildSlot(canvasRoot, font, i));

            // 全部先隐藏
            foreach (var s in Slots) s.Root.SetActive(false);
        }

        private static Slot BuildSlot(Transform canvasRoot, Font font, int index)
        {
            var go = UiBuilder.CreateNode($"Notify{index}", canvasRoot);
            var rect = go.GetComponent<RectTransform>();

            // 锚定右下角；y 偏移在刷新时按堆叠序号计算
            UiBuilder.Place(rect,
                new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-Margin, Margin + index * (CardHeight + Gap)),
                new Vector2(CardWidth, CardHeight));

            var bg = UiBuilder.CreateImage("Bg", go.transform, AceTheme.WindowBg);
            UiBuilder.Stretch(bg.rectTransform, 0f, 0f, 0f, 0f);

            var accent = UiBuilder.CreateImage("Accent", go.transform, AceTheme.Danger);
            UiBuilder.Place(accent.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                Vector2.zero, new Vector2(4f, CardHeight));

            var title = UiBuilder.CreateText("Title", go.transform,
                "", font, 13, AceTheme.TextMain, TextAnchor.UpperLeft, FontStyle.Bold);
            UiBuilder.Place(title.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(16f, -10f), new Vector2(CardWidth - 32f, 20f));

            var message = UiBuilder.CreateText("Message", go.transform,
                "", font, 12, AceTheme.TextDim, TextAnchor.UpperLeft);
            UiBuilder.Place(message.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(16f, -32f), new Vector2(CardWidth - 32f, 32f));

            return new Slot
            {
                Root = go,
                Rect = rect,
                Background = bg,
                AccentBar = accent,
                Title = title,
                Message = message,
            };
        }

        /// <summary>
        /// 弹出一条通知。
        /// </summary>
        /// <param name="title">标题（如「检测到作弊行为」）。</param>
        /// <param name="message">正文（如「玩家名 —— 原因」）。</param>
        /// <param name="accent">左侧强调色，按风险等级传入。</param>
        /// <param name="duration">停留秒数。</param>
        public static void Show(string title, string message, Color accent, float duration)
        {
            if (!_built) return;

            var slot = AcquireSlot();
            if (slot == null) return;

            slot.Title.text = title;
            slot.Message.text = message;
            slot.AccentBar.color = accent;
            slot.ExpireTime = Time.time + duration;
            slot.InUse = true;
            slot.Root.SetActive(true);
        }

        /// <summary>取一个可用卡槽：优先空闲的，其次顶掉最老的一条。</summary>
        private static Slot AcquireSlot()
        {
            foreach (var s in Slots)
                if (!s.InUse) return s;

            // 都在用 → 顶掉最早过期的那条
            Slot oldest = null;
            foreach (var s in Slots)
                if (oldest == null || s.ExpireTime < oldest.ExpireTime) oldest = s;

            return oldest;
        }

        /// <summary>每帧刷新：处理到期隐藏。</summary>
        public static void Tick()
        {
            if (!_built) return;

            var now = Time.time;

            // 先回收过期的
            foreach (var s in Slots)
            {
                if (!s.InUse || now < s.ExpireTime) continue;
                s.InUse = false;
                s.Root.SetActive(false);
            }

            // 再按使用中的顺序从下往上重新排布，避免出现空位
            var stackIndex = 0;
            foreach (var s in Slots)
            {
                if (!s.InUse) continue;
                s.Rect.anchoredPosition = new Vector2(-Margin, Margin + stackIndex * (CardHeight + Gap));
                stackIndex++;
            }
        }

        /// <summary>按风险等级取对应的强调色。</summary>
        public static Color AccentOf(RiskLevel level) => level switch
        {
            RiskLevel.Confirmed  => AceTheme.Danger,
            RiskLevel.HighRisk   => AceTheme.Danger,
            RiskLevel.Suspicious => AceTheme.Warning,
            _                    => AceTheme.Success,
        };

        /// <summary>按风险等级取对应的标题文案。</summary>
        public static string TitleOf(RiskLevel level) => level switch
        {
            RiskLevel.Confirmed  => "已确认作弊",
            RiskLevel.HighRisk   => "检测到高危行为",
            RiskLevel.Suspicious => "命中检测规则",
            _                    => "提示",
        };
    }
}
