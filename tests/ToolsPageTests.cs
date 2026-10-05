using System;
using System.Collections.Generic;
using System.IO;
using ApexCheatEnder.Config;
using ApexCheatEnder.Core;
using BepInEx.Configuration;

namespace ApexCheatEnder.Core
{
    // 测试只需要配置默认文本，不加载互认模块及游戏依赖。
    internal static class AcePresence { public const string DefaultTag = "[ACE]"; }
}

namespace ApexCheatEnder.Tests
{
    internal static class ToolsPageTests
    {
        private static void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
        private static string Source(string file) => File.ReadAllText(Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../src", file)));

        internal static void History()
        {
            var history = new LocalEventHistory();
            var metrics = new Dictionary<string, float>();
            for (var i = 0; i < 40; i++) metrics[new string('k', 80) + i] = i;
            var item = new Violation(ViolationKind.Teleport, Severity.High, 1, new string('n', 100), 0,
                new string('d', 1000), metrics);
            history.Add(null);
            history.Add(item);
            metrics.Clear();
            var snapshot = history.LatestFirst()[0];
            Check(snapshot.PlayerName.Length == 64 && snapshot.Detail.Length == 512, "文本上限");
            Check(snapshot.Metrics.Count > 0 && snapshot.Metrics.Count <= 16, "指标独立有界");
            ((Dictionary<string, float>)snapshot.Metrics).Clear();
            Check(history.LatestFirst()[0].Metrics.Count > 0, "返回快照不能修改历史");
            for (var i = 1; i < 300; i++) history.Add(new Violation(ViolationKind.IllegalChat, Severity.Low, i, "p", i, "d"));
            Check(history.Count == 256 && history.TotalCount == 300, "事件容量与累计数");
            var items = history.LatestFirst();
            Check(items[0].Timestamp == 299 && items[255].Timestamp == 44, "淘汰顺序与倒序");
            items[0] = null;
            Check(history.LatestFirst()[0] != null, "返回数组独立");
        }

        internal static void Export()
        {
            var history = new LocalEventHistory();
            history.Add(new Violation(ViolationKind.IllegalChat, Severity.High, 1, "PRIVATE_NAME", 2,
                "PRIVATE_CHAT E:/private/config", new Dictionary<string, float> { ["PRIVATE_KEY"] = 123 }));
            var output = history.SafeExport(60);
            foreach (var secret in new[] { "PRIVATE_NAME", "PRIVATE_CHAT", "E:/private", "PRIVATE_KEY", "123" })
                Check(!output.Contains(secret), "导出泄露：" + secret);
            Check(output.Contains("total=1; retained=1") && output.Contains("IllegalChat"), "保留诊断摘要");
        }

        internal static void Confirmation()
        {
            var confirm = new DoubleClickConfirmation();
            Check(!confirm.Confirm(10) && confirm.Armed(10), "首次仅武装");
            Check(confirm.Confirm(15) && !confirm.Armed(15), "五秒边界确认且消费");
            Check(!confirm.Confirm(20) && !confirm.Confirm(25.01), "超时重新武装");
            confirm.Cancel();
            Check(!confirm.Armed(26), "取消确认");
            Check(!confirm.Confirm(double.NaN) && !confirm.Confirm(double.PositiveInfinity), "非法时间");
            Check(!confirm.Confirm(100) && !confirm.Confirm(90), "时间倒退不能直接确认");
            var ui = Source("UI/SettingsWindow.cs");
            Check(ui.Contains("if (!RestoreConfirmation.Confirm(Time.unscaledTime)) return;"), "恢复必须经确认");
            Check(ui.Split("RestoreConfirmation.Cancel();").Length >= 3, "关闭和换页取消确认");
        }

        internal static void Mapping()
        {
            var mapping = "Teleport=网络;SpeedHack=会议;Unknown=x";
            var original = RuleGroups.Resolve(ViolationKind.Teleport, mapping);
            var seen = new HashSet<string>();
            // 从 Names.Length 推导，不写死组数 —— 以后加分组这个测试不该跟着挂
            for (var i = 0; i < RuleGroups.Names.Length; i++)
            {
                mapping = RuleGroups.Cycle(ViolationKind.Teleport, mapping);
                seen.Add(RuleGroups.Resolve(ViolationKind.Teleport, mapping));
                Check(RuleGroups.Resolve(ViolationKind.SpeedHack, mapping) == "会议", "编辑不改其他映射");
            }
            Check(seen.Count == RuleGroups.Names.Length && RuleGroups.Resolve(ViolationKind.Teleport, mapping) == original,
                "全部 " + RuleGroups.Names.Length + " 组完整循环后回到原值");
            Check(RuleGroups.Resolve(ViolationKind.Teleport, "Teleport=非法") == "移动", "非法映射回退");
            Check(RuleGroups.Resolve(ViolationKind.Teleport, new string('x', 8193)) == "移动", "超长映射回退");
            var history = new LocalEventHistory();
            history.Add(new Violation(ViolationKind.Teleport, Severity.High, 1, "p", 0, "d"));
            Check(RuleGroups.Filter(history, 5, mapping).Length == 1, "历史使用自定义网络映射");
            Check(RuleGroups.Filter(history, 1, mapping).Length == 0, "不再按内置移动组过滤");
            Check(RuleGroups.Filter(history, 0, mapping).Length == 1, "全部历史");
            var ui = Source("UI/SettingsWindow.cs");
            // 规则分组不再在界面里逐条编辑 —— 把 40 多条规则各占一行太啰嗦，
            // 而且几乎没人会改。要调就改配置文件，历史过滤仍然读同一个映射。
            Check(ui.Contains("RuleGroups.Filter("), "历史必须按配置的映射过滤");
            Check(!ui.Contains("RuleGroups.Cycle("), "界面不再逐条列出全部规则");
        }

        internal static void ConfigSafety()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ace-tools-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "test.cfg");
                var cfg = new AntiCheatConfig(new ConfigFile(path, false));
                Check(!cfg.ShowRepeatedChatNotice.Value, "重复聊天默认关闭");
                cfg.AllowAutoKick.Value = true;
                cfg.DispositionMode.Value = DispositionModes.Ban;
                cfg.ChatAbuseKeywords.Value = "custom";
                cfg.RuleGroupMapping.Value = RuleGroups.Cycle(ViolationKind.Teleport, RuleGroups.Defaults());
                var mapping = cfg.RuleGroupMapping.Value;
                for (var preset = 0; preset < 3; preset++)
                {
                    cfg.ApplyPreset(preset);
                    Check(cfg.AllowAutoKick.Value && cfg.DispositionMode.Value == DispositionModes.Ban, "预设不修改危险处置");
                    Check(cfg.RuleGroupMapping.Value == mapping && cfg.ChatAbuseKeywords.Value == "custom", "预设保留自定义");
                }
                cfg.AllowAutoKick.Value = false;
                cfg.DispositionMode.Value = DispositionModes.Warn;
                cfg.ApplyPreset(2);
                Check(!cfg.AllowAutoKick.Value && cfg.DispositionMode.Value == DispositionModes.Warn, "严格预设不启用踢人");
                cfg.ShowRepeatedChatNotice.Value = true;
                cfg.RestoreSafeDefaults();
                Check(!cfg.ShowRepeatedChatNotice.Value && !cfg.AllowAutoKick.Value, "恢复安全默认");
                Check(cfg.RuleGroupMapping.Value == mapping && cfg.ChatAbuseKeywords.Value == "custom", "恢复保留文本映射");
                cfg.File.Save();
                var restored = new AntiCheatConfig(new ConfigFile(path, false));
                Check(restored.RuleGroupMapping.Value == mapping, "分组真实落盘重读");
            }
            finally { Directory.Delete(directory, true); }
        }

        internal static void RuntimeIntegration()
        {
            var ui = Source("UI/ChatAbuseNotice.cs");
            Check(ui.Contains("new ChatRepeatNoticePolicy()") && ui.Contains("RepeatPolicy.TryNotice(message.PlayerId"), "真实运行时使用被测发送者策略");
            Check(ui.Contains("cfg.RepeatedChatThreshold.Value") && ui.Contains("RepeatPolicy.Reset();"), "阈值与清理接线");
            Check(!ui.Contains("Submit(") && !ui.Contains("KickPlayer(") && !ui.Contains("AddEvidence("), "本地提示不产生证据处置");
            var patch = Source("Patches/GameplayPatches.cs");
            Check(patch.Contains("ChatAbuseNotice.Observe(") && patch.Contains("typeof(ChatController), \"AddChat\""), "聊天呈现覆盖远端发送者且只计一次");
            var plugin = Source("AntiCheatPlugin.cs");
            Check(plugin.IndexOf("TryPatch(typeof(ChatNoticePatch)") < plugin.IndexOf("if (cfg.EnableEventScan.Value)"), "本地提示独立于事件扫描挂载");
            Check(!plugin.Contains("按 F8"), "删除失效快捷键提示");
            var root = Source("UI/AceUiRoot.cs");
            Check(root.Contains("ChatAbuseNotice.Tick("), "帧入口驱动聊天策略");
        }
    }
}
