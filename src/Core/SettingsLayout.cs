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
        /// <summary>
        /// 行高。
        ///
        /// 说明文字现在**常驻在行内**（第二行小字），不再靠悬停才显示 ——
        /// 悬停才能看到说明，等于「不把鼠标挨个划过去就不知道这项是干什么的」。
        /// 带说明的行给两行高度，没有说明的保持单行。
        /// </summary>
        public static float RowHeight(bool hasHint) => hasHint ? 62f : 46f;

        /// <summary>分组标题（小号分隔文字）占的高度。</summary>
        public const float SectionCaptionHeight = 30f;
        public static float MaxScroll(float contentHeight, float viewportHeight) => Math.Max(0f, contentHeight - viewportHeight);
        public static int Group(int page, int row)
        {
            switch (page)
            {
                // 常规：检测模块 / 界面与提示 / 玩家标记 / 事件与消息 / 日志与聊天
                case 0: return row < 4 ? 0 : row < 6 ? 1 : row < 8 ? 2 : row < 12 ? 3 : 4;
                // 行为检测：移动 / 击杀与任务 / 会议与聊天
                case 1: return row < 6 ? 0 : row < 10 ? 1 : 2;
                // 处置规则：处置与记录 / 当前命中玩家
                case 2: return row < 4 ? 0 : 1;
                // 网络防护：通风管与滑索 / 网络层 / 进阶检测
                case 3: return row < 7 ? 0 : row < 11 ? 1 : 2;
                // 关于：版本与快捷键 / 配置与帮助
                case 4: return row < 2 ? 0 : 1;
                // 记录与工具：检测记录 / 工具与预设 / 其他
                default: return row < 7 ? 0 : row < 10 ? 1 : 2;
            }
        }

        public static readonly string[][] GroupNames =
        {
            new[] { "检测模块", "界面与提示", "玩家标记", "事件与消息", "日志与聊天" },
            new[] { "移动", "击杀与任务", "会议与聊天" },
            new[] { "处置与记录", "当前命中玩家" },
            new[] { "通风管与滑索", "网络层", "进阶检测" },
            new[] { "版本与快捷键", "配置与帮助" },
            new[] { "检测记录", "工具与预设", "其他" }
        };

        /// <summary>
        /// 每个页面的子标签：名称 + 它覆盖的分组下标。
        ///
        /// **为什么需要子标签**：把 14~16 行塞在一页里，用户得一直往下滚，
        /// 而且找不到东西 —— 一屏里同时出现「检测阈值」「玩家标记」「窗口分辨率」
        /// 三件互不相干的事，没有哪一件是「一眼能看到」的。
        /// 拆成 3~5 个子标签后，每屏只剩 2~6 行，翻一下就看完了。
        ///
        /// 一个子标签可以覆盖多个分组（分组不必连续）——
        /// 过滤是按分组下标做的，行仍按原顺序排列，各自带自己的分组标题。
        ///
        /// 只有一个子标签的页面不画子标签栏（画了也没得切）。
        /// </summary>
        public static readonly (string Name, int[] Groups)[][] SubTabs =
        {
            // 0 常规
            new[]
            {
                ("检测模块", new[] { 0 }),
                ("界面与提示", new[] { 1, 4 }),
                ("玩家标记", new[] { 2 }),
                ("事件与消息", new[] { 3 }),
            },
            // 1 行为检测
            new[]
            {
                ("移动", new[] { 0 }),
                ("击杀与任务", new[] { 1 }),
                ("会议与聊天", new[] { 2 }),
            },
            // 2 处置规则
            new[]
            {
                ("处置与记录", new[] { 0 }),
                ("当前命中玩家", new[] { 1 }),
            },
            // 3 网络防护
            new[]
            {
                ("通风管与滑索", new[] { 0 }),
                ("网络层", new[] { 1 }),
                ("进阶检测", new[] { 2 }),
            },
            // 4 关于
            new[]
            {
                ("版本与快捷键", new[] { 0 }),
                ("配置与帮助", new[] { 1 }),
            },
            // 5 记录与工具
            new[]
            {
                ("检测记录", new[] { 0 }),
                ("工具与预设", new[] { 1 }),
                ("其他", new[] { 2 }),
            },
        };

        /// <summary>取某页的子标签定义；越界返回空数组。</summary>
        public static (string Name, int[] Groups)[] SubTabsFor(int page)
            => page >= 0 && page < SubTabs.Length ? SubTabs[page] : Array.Empty<(string, int[])>();

        /// <summary>某一行在指定页面、指定子标签下是否应该显示。</summary>
        public static bool RowVisible(int page, int row, int subTab)
        {
            var defs = SubTabsFor(page);
            if (defs.Length <= 1) return true;                       // 没得切，全显示
            if (subTab < 0 || subTab >= defs.Length) return true;
            return Array.IndexOf(defs[subTab].Groups, Group(page, row)) >= 0;
        }
    }
}
