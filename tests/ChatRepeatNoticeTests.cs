using System;
using ApexCheatEnder.Core;

namespace ApexCheatEnder.Tests
{
    internal static class ChatRepeatNoticeTests
    {
        internal static void Run()
        {
            var policy = new ChatRepeatNoticePolicy();
            Check(!policy.TryNotice(1, "测试", false, 0), "默认禁用不提示");
            Check(policy.TrackedSenderCount == 0, "禁用不保留聊天数据");
            Check(!policy.TryNotice(1, "测试", true, 0), "第一次不提示");
            Check(!policy.TryNotice(1, "测试", true, 1), "第二次不提示");
            Check(policy.TryNotice(1, "测试", true, 2), "第三次提示");
            Check(!policy.TryNotice(1, "测试", true, 3), "同发送者冷却");
            Check(!policy.TryNotice(2, "测试", true, 3), "不同发送者独立计数");
            Check(!policy.TryNotice(2, "测试", true, 4), "不同发送者第二次");
            Check(policy.TryNotice(2, "测试", true, 5), "不同发送者独立冷却");
            policy.Reset();
            for (var i = 0; i < 4; i++) Check(!policy.TryNotice(1, "测试", true, 4, 5), "配置五次阈值");
            Check(policy.TryNotice(1, "测试", true, 4, 5), "第五次提示");
            for (var i = 5; i <= 13; i++) Check(!policy.TryNotice(1, "测试", true, i, 5), "持续发送仍受冷却");
            Check(policy.TryNotice(1, "测试", true, 14, 5), "恰好十秒冷却可再提示");
            policy.Reset();
            Check(!policy.TryNotice(1, "ＡＢＣ", true, 1), "全角第一次");
            Check(!policy.TryNotice(1, " abc ", true, 2), "规范化第二次");
            Check(policy.TryNotice(1, "ABC", true, 3), "规范化一致");
            policy.Reset();
            policy.TryNotice(1, "甲", true, 0);
            policy.TryNotice(1, "甲", true, 1);
            Check(!policy.TryNotice(1, "乙", true, 2), "不同文本重置");
            Check(!policy.TryNotice(1, "甲", true, 3), "不累计不连续文本");
            policy.Reset();
            policy.TryNotice(1, "测试", true, 0);
            policy.TryNotice(1, "测试", true, 1);
            Check(!policy.TryNotice(1, "测试", true, 11), "超时重新计数");
            policy.TryNotice(1, "测试", true, 12);
            Check(!policy.TryNotice(1, "测试", true, 0), "时钟倒退重新计数");
            policy.TryNotice(1, "测试", false, 1);
            Check(policy.TrackedSenderCount == 0, "禁用及时清理");
            Check(!policy.TryNotice(1, "测试", true, double.NaN), "非法时间不提示");
            Check(!policy.TryNotice(1, "测试", true, double.PositiveInfinity), "无限时间不提示");
            Check(!policy.TryNotice(-1, "测试", true, 1), "未知发送者不计数");
            Check(!policy.TryNotice(1, new string('a', 513), true, 1), "超长文本不保留");
            Check(!policy.TryNotice(1, "\uD800", true, 1), "非法UTF16不抛异常");
            Check(policy.TrackedSenderCount == 0, "非法输入无状态");
            for (var i = 0; i < 100; i++) policy.TryNotice(i, "测试", true, i);
            Check(policy.TrackedSenderCount == ChatRepeatNoticePolicy.MaxTrackedSenders, "发送者历史有界");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
