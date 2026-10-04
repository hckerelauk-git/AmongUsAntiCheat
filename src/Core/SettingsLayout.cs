using System;

namespace ApexCheatEnder.Core
{
    internal static class SettingsLayout
    {
        public static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
        public static float Normalize(float value, float min, float max) => max <= min ? 0f : Clamp((value - min) / (max - min), 0f, 1f);
        public static float SliderValue(float fraction, float min, float max, float step, bool integer)
        {
            if (max <= min) return min;
            var value = min + Clamp(fraction, 0f, 1f) * (max - min);
            if (step > 0f) value = min + (float)Math.Round((value - min) / step, MidpointRounding.AwayFromZero) * step;
            if (integer) value = (float)Math.Round(value, MidpointRounding.AwayFromZero);
            return Clamp(value, min, max);
        }
        /// <summary>
        /// 设置行的高度。
        ///
        /// 统一 46：说明文字已经移到底部描述栏，行内只剩「名称 + 控件」，
        /// 不再需要为说明预留第二行（原来是 100/68）。
        /// </summary>
        public static float RowHeight(bool number) => 46f;

        /// <summary>分组标题（小号分隔文字）占的高度。</summary>
        public const float SectionCaptionHeight = 30f;
        public static float MaxScroll(float contentHeight, float viewportHeight) => Math.Max(0f, contentHeight - viewportHeight);
        public static int Group(int page, int row)
        {
            switch (page)
            {
                case 0: return row < 4 ? 0 : row < 6 ? 1 : row < 8 ? 2 : row < 12 ? 3 : row < 13 ? 4 : 5;
                case 1: return row < 6 ? 0 : row < 10 ? 1 : 2;
                case 2: return row < 2 ? 0 : row < 4 ? 1 : 2;
                case 3: return row < 7 ? 0 : row < 11 ? 1 : 2;
                default: return row < 2 ? 0 : 1;
            }
        }
        public static readonly string[][] GroupNames =
        {
            new[] { "检测总开关", "界面显示", "ACE 玩家标记", "事件与消息检测", "诊断日志", "本地聊天提示（可能误报）" },
            new[] { "移动检测阈值", "击杀与任务阈值", "会议与聊天阈值" },
            new[] { "自动处置", "历史记录", "当前命中玩家" },
            new[] { "通风管与滑索", "数据包与位置同步", "会议与数据包校验" },
            new[] { "版本与快捷键", "配置与帮助" }
        };
    }
}
