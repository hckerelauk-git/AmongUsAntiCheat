using System;
using System.Collections.Generic;
using ApexCheatEnder.Core;

namespace ApexCheatEnder.Tests
{
    /// <summary>
    /// 零依赖测试 runner。
    ///
    /// 约定：每个 Test* 方法用 Assert 系列做断言，失败即抛异常，
    /// 由 Main 捕获并计入失败数；全部通过时退出码 0，否则 1。
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;
        private static readonly List<string> Failures = new List<string>();

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("Apex Cheat Ender · 核心逻辑测试");
            Console.WriteLine(new string('-', 56));

            Run("UI：滑条下界", () => Eq(0.5f, SettingsLayout.SliderValue(-1f, 0.5f, 20f, 0.5f, false), "下界"));
            Run("UI：滑条上界", () => Eq(20f, SettingsLayout.SliderValue(2f, 0.5f, 20f, 0.5f, false), "上界"));
            Run("UI：浮点步长映射", () => Eq(0.51f, SettingsLayout.SliderValue(0.5f, 0.02f, 1f, 0.01f, false), "浮点映射"));
            Run("UI：整数滑条舍入", () => Eq(11f, SettingsLayout.SliderValue(0.5f, 1f, 20f, 1f, true), "整数映射"));
            Run("UI：零范围", () => Eq(5f, SettingsLayout.SliderValue(0.9f, 5f, 5f, 1f, true), "零范围"));
            Run("UI：归一化中点", () => Eq(0.5f, SettingsLayout.Normalize(3f, 1f, 5f), "归一化"));
            Run("UI：外部值归一化钳制", () => Eq(1f, SettingsLayout.Normalize(100f, 1f, 5f), "钳制"));
            Run("UI：归一化零范围", () => Eq(0f, SettingsLayout.Normalize(5f, 5f, 5f), "零范围"));
            Run("UI：折叠仅保留标题", () => Eq(38f, SettingsLayout.CardHeight(400f, true), "折叠高度"));
            Run("UI：展开高度包含内边距", () => Eq(444f, SettingsLayout.CardHeight(400f, false), "展开高度"));
            Run("UI：短页无滚动", () => Eq(0f, SettingsLayout.MaxScroll(100f, 300f), "短页"));
            Run("UI：长页末行可达", () => Eq(200f, SettingsLayout.MaxScroll(500f, 300f), "长页"));
            Run("UI：行为子页全部选项唯一覆盖", TestSettingsGroups);
            Run("UI：数字行轨道不挤说明", () => True(SettingsLayout.RowHeight(true) > SettingsLayout.RowHeight(false) + 24f, "滑条独立区域"));
            Run("背景：首次选择覆盖三张内置图", TestMenuArtInitialSelection);
            Run("背景：三图连续选择不重复", TestMenuArtNoRepeat);
            Run("背景：实际图片资源存在", TestMenuArtResourceFiles);
            Run("聊天提示：关键词匹配", TestChatAbuseMatches);
            Run("聊天提示：规范化与边界", TestChatAbuseNormalizationAndBoundaries);
            Run("聊天提示：不匹配不提示", TestChatAbuseNoMatch);
            Run("聊天提示：关闭不提示", TestChatAbuseDisabled);
            Run("聊天提示：10秒冷却", TestChatAbuseCooldown);
            Run("聊天提示：非法时间不提示", TestChatAbuseInvalidTime);
            Run("聊天提示：重复刷屏有界与仅本地提示", ChatRepeatNoticeTests.Run);
            Run("聊天提示：重试资源清理源码约束", () =>
            {
                var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/UI/ChatAbuseNotice.cs"));
                var notice = System.IO.File.ReadAllText(path);
                True(!notice.Contains("_failed"), "不允许永久禁用");
                True(notice.Contains("now < _nextBuildAttempt") && notice.Contains("now >= _nextBuildAttempt"), "重试必须按时间节流");
                True(notice.Contains("DestroyBuilt();") && notice.Contains("Math.Min(30d"), "失败须清理半成品且限制退避上限");
            });
            if (args.Length == 2)
                Run("背景：实际 DLL 资源名称与图片字节", () => TestMenuArtResources(args[0], args[1]));
            Run("背景：自动内置与菜单入口源码约束", TestAutomaticMenuArt);
            Run("背景：等比居中裁切", TestMenuArtCrop);
            Run("GameVec2：距离与向量运算", TestGameVec2);
            Run("PlayerTrack：快照位移/时间差计算", TestPlayerTrackDelta);
            Run("PlayerTrack：合法传送豁免窗口", TestLegalTeleportWindow);
            Run("PlayerTrack：回合重置清空回合级状态", TestPlayerTrackReset);
            Run("PlayerTracker：按 Id 取或建轨迹", TestTracker);
            Run("Violation：严重度到基础权重的映射", TestViolationWeight);
            Run("PlayerVerdict：风险等级四态判定", TestEvaluateLevel);
            Run("PlayerVerdict：证据累加与 Critical 计数", TestAddEvidence);
            Run("PlayerVerdict：Reset 清空本局命中", TestVerdictReset);
            Run("工具：有界历史独立快照", ToolsPageTests.History);
            Run("工具：脱敏导出", ToolsPageTests.Export);
            Run("工具：二次确认边界与取消", ToolsPageTests.Confirmation);
            Run("工具：六组编辑和历史映射", ToolsPageTests.Mapping);
            Run("工具：预设安全与映射持久化", ToolsPageTests.ConfigSafety);
            Run("聊天：真实运行时集成约束", ToolsPageTests.RuntimeIntegration);

            Console.WriteLine(new string('-', 56));
            Console.WriteLine($"通过 {_passed} ／ 失败 {_failed}");

            if (_failed > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败详情：");
                foreach (var f in Failures) Console.WriteLine("  ✗ " + f);
                return 1;
            }

            Console.WriteLine("全部通过 ✓");
            return 0;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("  ✓ " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Failures.Add($"{name} → {ex.Message}");
                Console.WriteLine("  ✗ " + name);
            }
        }

        // ================= 断言 =================

        private static void True(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        private static void Eq(float expected, float actual, string msg, float eps = 0.0001f)
        {
            if (Math.Abs(expected - actual) > eps)
                throw new Exception($"{msg}：期望 {expected}，实际 {actual}");
        }

        private static void Eq(int expected, int actual, string msg)
        {
            if (expected != actual)
                throw new Exception($"{msg}：期望 {expected}，实际 {actual}");
        }

        private static void Eq<T>(T expected, T actual, string msg) where T : struct
        {
            if (!expected.Equals(actual))
                throw new Exception($"{msg}：期望 {expected}，实际 {actual}");
        }

        private static void TestSettingsGroups()
        {
            var expected = new[] { 6, 4, 3 };
            var counts = new int[3];
            for (var row = 0; row < 13; row++) counts[SettingsLayout.Group(1, row)]++;
            for (var group = 0; group < 3; group++) Eq(expected[group], counts[group], "子页选项数");
            var pageCounts = new[] { 15, 13, 4, 14, 7 };
            for (var page = 0; page < pageCounts.Length; page++)
                for (var row = 0; row < pageCounts[page]; row++)
                    True(SettingsLayout.Group(page, row) < SettingsLayout.GroupNames[page].Length, "分组标题存在");
            Eq(2, SettingsLayout.Group(2, 100), "动态处置玩家属于命中卡片");
        }

        private static void TestMenuArtInitialSelection()
        {
            Eq(3, MenuArtSource.BackgroundCount, "随机候选应为三张图片");
            var seen = new HashSet<int>();
            var random = new Random(20261003);
            for (var i = 0; i < 100; i++)
            {
                var selected = MenuArtSource.SelectBackground(-1, random);
                True(selected >= 0 && selected < MenuArtSource.BackgroundCount, "选择必须属于三图候选范围");
                seen.Add(selected);
            }
            True(seen.SetEquals(new[] { 0, 1, 2 }), "首次选择应覆盖全部三张图片");
        }

        private static void TestMenuArtNoRepeat()
        {
            var random = new Random(1);
            for (var initial = 0; initial < MenuArtSource.BackgroundCount; initial++)
            {
                var seen = new HashSet<int> { initial };
                var previous = initial;
                for (var i = 0; i < 1000; i++)
                {
                    var next = MenuArtSource.SelectBackground(previous, random);
                    True(next >= 0 && next < MenuArtSource.BackgroundCount, "选择必须属于三图候选范围");
                    True(next != previous, "连续两次不能选择同一张图片");
                    seen.Add(next);
                    previous = next;
                }
                True(seen.SetEquals(new[] { 0, 1, 2 }), "连续选择应覆盖全部三张图片，而非固定两图交替");
            }
        }

        private static void TestMenuArtResourceFiles()
        {
            var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/Resources"));
            foreach (var resource in new[] { MenuArtSource.FirstResource, MenuArtSource.SecondResource, MenuArtSource.ThirdResource, MenuArtSource.FallbackResource })
            {
                var path = System.IO.Path.Combine(directory, resource.Substring("ApexCheatEnder.".Length));
                True(System.IO.File.Exists(path), "图片资源应实际存在：" + path);
                var bytes = System.IO.File.ReadAllBytes(path);
                True(bytes.Length > 4 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
                    "图片资源应为非空 JPEG：" + resource);
            }
        }

        private static void TestChatAbuseMatches()
        {
            True(ChatAbuseNoticePolicy.Matches("前缀 " + ChatAbuseNoticePolicy.DefaultKeywords.Split(',')[0] + " 后缀",
                ChatAbuseNoticePolicy.DefaultKeywords), "默认关键词应支持子串匹配");
            foreach (var separator in new[] { ",", "，", ";", "；", "\n", "\r" })
                True(ChatAbuseNoticePolicy.Matches("包含 TARGET 的消息", "  other " + separator + " target  "),
                    "关键词应支持分隔、去空白及忽略大小写");
            True(new ChatAbuseNoticePolicy().TryShow("TARGET", "target", true, 0), "启用后首次匹配应提示");
        }

        private static void TestChatAbuseNormalizationAndBoundaries()
        {
            True(ChatAbuseNoticePolicy.Matches("【傻逼】！", "傻逼"), "短词被常见聊天标点包围时应命中");
            True(ChatAbuseNoticePolicy.Matches("xx[TARGET]yy", "target"), "标点应保留词边界");
            True(!ChatAbuseNoticePolicy.Matches("targeting", "target"), "关键词嵌入英文单词不应误报");
            True(!ChatAbuseNoticePolicy.Matches("前缀傻逼后缀", "傻逼"), "短中文词嵌入其他文本应保守拒绝");
            True(!ChatAbuseNoticePolicy.Matches("tar-get", "target"), "不可删除标点拼接新关键词");
            True(ChatAbuseNoticePolicy.Matches("ＴＡＲＧＥＴ！", "target"), "全角与大小写应规范化");
            True(ChatAbuseNoticePolicy.Matches("hello，world", "hello world"), "标点与空白统一为分隔符");
            True(!ChatAbuseNoticePolicy.Matches("\ud800", "target"), "无效 Unicode 不应抛异常");
            True(!ChatAbuseNoticePolicy.Matches("普通消息", "!!!"), "纯标点关键词不可匹配");
            True(ChatAbuseNoticePolicy.Matches("targeting target", "target"), "应继续寻找后续合法命中");
            foreach (var keyword in ChatAbuseNoticePolicy.DefaultKeywords.Split(','))
                True(ChatAbuseNoticePolicy.Matches("（" + keyword + "）！", ChatAbuseNoticePolicy.DefaultKeywords), "保留全部默认关键词");
        }

        private static void TestChatAbuseNoMatch()
        {
            foreach (var text in new[] { null, "", "   ", "普通消息" })
                True(!ChatAbuseNoticePolicy.Matches(text, "target"), "空消息或不匹配消息应拒绝");
            foreach (var keywords in new[] { null, "", "   ", ",，;；\n\r  " })
                True(!ChatAbuseNoticePolicy.Matches("target", keywords), "空关键词应拒绝");
            var policy = new ChatAbuseNoticePolicy();
            True(!policy.TryShow("普通消息", "target", true, 0), "不匹配不应提示");
            True(policy.TryShow("target", "target", true, 0), "不匹配不应占用冷却");
        }

        private static void TestChatAbuseDisabled()
        {
            var policy = new ChatAbuseNoticePolicy();
            True(!policy.TryShow("target", "target", false, 0), "关闭时匹配也不应提示");
            True(policy.TryShow("target", "target", true, 0), "关闭时不应占用冷却");
        }

        private static void TestChatAbuseCooldown()
        {
            True(ChatAbuseNoticePolicy.CooldownSeconds == 10, "冷却应为10秒");
            var policy = new ChatAbuseNoticePolicy();
            True(policy.TryShow("first", "first,second", true, 100), "首次匹配应提示");
            foreach (var now in new[] { 100d, 105d, 109.999d })
                True(!policy.TryShow("second", "first,second", true, now), "10秒内不同关键词也不能再次提示");
            True(policy.TryShow("second", "first,second", true, 110), "恰好10秒应允许提示，拒绝不应延长冷却");
            True(!policy.TryShow("first", "first,second", true, 119.999), "再次提示后应重新冷却");
            True(policy.TryShow("first", "first,second", true, 120), "第二次冷却边界应允许提示");
        }

        private static void TestChatAbuseInvalidTime()
        {
            var policy = new ChatAbuseNoticePolicy();
            foreach (var now in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                True(!policy.TryShow("target", "target", true, now), "非有限时间不应提示");
            True(policy.TryShow("target", "target", true, 100), "非法时间不应污染首次提示状态");
            True(!policy.TryShow("target", "target", true, 99), "时间倒退应拒绝提示");
            foreach (var now in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                True(!policy.TryShow("target", "target", true, now), "冷却期间非法时间也应拒绝");
            True(!policy.TryShow("target", "target", true, 109), "非法时间不应清除冷却");
            True(policy.TryShow("target", "target", true, 110), "非法时间不应改变冷却边界");
        }

        private static void TestMenuArtResources(string dllPath, string resourceDirectory)
        {
            // 只读取 PE 元数据，不加载插件或游戏程序集。
            using var input = System.IO.File.OpenRead(dllPath);
            using var pe = new System.Reflection.PortableExecutable.PEReader(input);
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            var names = new HashSet<string>();
            var expected = new HashSet<string>
                { MenuArtSource.FirstResource, MenuArtSource.SecondResource, MenuArtSource.ThirdResource, MenuArtSource.FallbackResource };
            foreach (var handle in metadata.ManifestResources)
            {
                var resource = metadata.GetManifestResource(handle);
                var name = metadata.GetString(resource.Name);
                if (!expected.Contains(name)) continue;
                True(resource.Implementation.IsNil, "图片必须嵌入 DLL，不能链接外部文件");
                var section = pe.GetSectionData(pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress);
                var reader = section.GetReader((int)resource.Offset, section.Length - (int)resource.Offset);
                var bytes = reader.ReadBytes(reader.ReadInt32());
                var fileName = name.Substring("ApexCheatEnder.".Length);
                var source = System.IO.File.ReadAllBytes(System.IO.Path.Combine(resourceDirectory, fileName));
                True(System.Linq.Enumerable.SequenceEqual(source, bytes), "内嵌图片字节应与资源文件一致：" + name);
                names.Add(name);
            }
            True(names.SetEquals(expected), "三张候选图及旧兜底图应以明确名称嵌入 DLL");
        }

        private static void TestAutomaticMenuArt()
        {
            var source = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src"));
            var art = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "UI/MainMenuArt.cs"));
            foreach (var forbidden in new[] { "ShowMainMenuArt", "MainMenuArtUrl", "DownloadAsync", "Task.Run", "ChangeBackground" })
                True(!art.Contains(forbidden), "自动背景不得依赖旧配置、联网或手动切换：" + forbidden);
            True(art.Contains("static readonly int SelectedBackground"), "启动选择必须固定");
            True(art.Contains("SceneManager.GetActiveScene().handle"), "必须检查当前菜单场景");
            True(!art.Contains("new GameObject") && !art.Contains("sortingOrder ="), "无安全后景证据时不得创建覆盖层或改变排序");
            True(art.Contains("renderer.name == \"Background\""), "仅替换明确 Background");
            var settings = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "UI/SettingsWindow.cs"));
            foreach (var forbidden in new[] { "ShowMainMenuArt", "MainMenuArtUrl", "ChangeBackground", "换一张" })
                True(!settings.Contains(forbidden), "设置中不得保留背景入口");
            True(settings.Contains("cfg.ShowChatAbuseNotice") && settings.Contains("cfg.ChatAbuseKeywords"), "原聊天提示设置保留");
            var plugin = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "AntiCheatPlugin.cs"));
            True(plugin.IndexOf("TryPatch(typeof(MainMenuArtStartPatch)") < plugin.IndexOf("if (cfg.EnableEventScan.Value)"),
                "背景入口须独立于事件检测开关挂载");
            var patch = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "Patches/MenuPatches.cs"));
            True(patch.Contains("typeof(MainMenuManager)") && !patch.Contains("EventScanEnabled"), "菜单使用真实类型入口且不读检测开关");
        }

        private static void TestMenuArtCrop()
        {
            var wide = MenuArtSource.Crop(2000, 1000, 1f);
            Eq(500f, wide.X, "宽图居中裁切");
            Eq(1000f, wide.Width, "宽图裁切宽度");
            var tall = MenuArtSource.Crop(1000, 2000, 2f);
            Eq(750f, tall.Y, "高图居中裁切");
            Eq(500f, tall.Height, "高图裁切高度");
            var exact = MenuArtSource.Crop(1600, 900, 1600f / 900f);
            Eq(1600f, exact.Width, "等比例不裁切宽度");
            Eq(900f, exact.Height, "等比例不裁切高度");
        }

        // ================= GameVec2 =================

        private static void TestGameVec2()
        {
            var a = new GameVec2(0f, 0f);
            var b = new GameVec2(3f, 4f);

            Eq(5f, GameVec2.Distance(a, b), "3-4-5 直角三角形斜边");
            Eq(25f, GameVec2.SqrDistance(a, b), "平方距离");
            Eq(25f, b.SqrMagnitude, "向量自身平方长度");
            Eq(5f, b.Magnitude, "向量长度");

            var sum = a + b;
            Eq(3f, sum.X, "加法 X");
            Eq(4f, sum.Y, "加法 Y");

            var diff = b - a;
            Eq(3f, diff.X, "减法 X");

            // 中点在 0.5 处
            var mid = GameVec2.Lerp(a, b, 0.5f);
            Eq(1.5f, mid.X, "插值 X");
            Eq(2f, mid.Y, "插值 Y");

            // 零向量
            Eq(0f, GameVec2.Zero.X, "Zero.X");
            Eq(0f, GameVec2.Distance(a, a), "同点距离为 0");
        }

        // ================= PlayerTrack =================

        private static void TestPlayerTrackDelta()
        {
            var track = new PlayerTrack(1, "P1", 0f);
            True(!track.HasPrevious, "初始不应有上一帧");

            track.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f) });
            True(track.HasPrevious, "Push 后应标记有上一帧");

            // 1 秒移动 3-4-5 → 位移 5，速度 5
            track.Push(new PlayerSnapshot { Time = 1f, Position = new GameVec2(3f, 4f) });

            Eq(5f, track.Current.DeltaDistance, "位移应为 5");
            Eq(1f, track.Current.DeltaTime, "时间差应为 1");
            Eq(5f, track.Current.Speed, "速度应为 5");
        }

        private static void TestLegalTeleportWindow()
        {
            var track = new PlayerTrack(1, "P1", 0f);

            // 初始：LastLegalTeleportTime 为负无穷 → 任何时刻都不在豁免窗口内
            True(!track.IsInLegalTeleportWindow(100f), "未标记时不应处于豁免窗口");

            track.LastLegalTeleportTime = 10f;

            True(track.IsInLegalTeleportWindow(10.2f), "0.2s 后应在默认 0.35s 窗口内");
            // 注意：不要拿「正好等于窗口边界」的值做断言。
            // 10.35f - 10f 在 float 下是 0.35000038，比 0.35f 略大，
            // 边界比较会因浮点误差翻转。测边界两侧的安全距离即可。
            True(track.IsInLegalTeleportWindow(10.34f), "0.34s 应仍在窗口内");
            True(!track.IsInLegalTeleportWindow(10.36f), "0.36s 应已出窗口");
            True(!track.IsInLegalTeleportWindow(11f), "1s 后应已出窗口");

            // 自定义窗口
            True(track.IsInLegalTeleportWindow(11f, 1.5f), "自定义 1.5s 窗口内");
        }

        private static void TestPlayerTrackReset()
        {
            var track = new PlayerTrack(1, "P1", 0f);
            track.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f) });
            track.Push(new PlayerSnapshot { Time = 1f, Position = new GameVec2(10f, 0f) });
            track.ConsecutiveSpeedStrikes = 5;
            track.ConsecutiveTaskStrikes = 3;
            track.FlaggedThisRound = true;
            track.LastKillTime = 5f;
            track.LastTaskTime = 6f;
            track.LastLegalTeleportTime = 7f;

            track.ResetForNewRound(100f);

            Eq(0, track.ConsecutiveSpeedStrikes, "超速计数应清零");
            Eq(0, track.ConsecutiveTaskStrikes, "任务计数应清零");
            True(!track.FlaggedThisRound, "本局标记应清除");
            True(!track.HasPrevious, "应清掉上一帧");
            Eq(100f, track.FirstSeenTime, "FirstSeenTime 应更新为新回合时间");
            True(float.IsNegativeInfinity(track.LastKillTime), "击杀时间应重置为负无穷");
            True(float.IsNegativeInfinity(track.LastLegalTeleportTime), "豁免时间应重置为负无穷");
            Eq(0, track.History.Count, "历史队列应清空");
        }

        private static void TestTracker()
        {
            var tracker = new PlayerTracker();

            var a = tracker.GetOrCreate(7, "Alpha", 0f);
            var again = tracker.GetOrCreate(7, "Alpha", 1f);
            True(ReferenceEquals(a, again), "同一 Id 应返回同一实例");

            // 名字可被更新
            tracker.GetOrCreate(7, "AlphaRenamed", 2f);
            True(a.Name == "AlphaRenamed", "名字应被新值覆盖");

            // 空名字不覆盖已有名字
            tracker.GetOrCreate(7, "", 3f);
            True(a.Name == "AlphaRenamed", "空名字不应覆盖");

            True(tracker.TryGet(7, out var found) && ReferenceEquals(found, a), "TryGet 应命中");
            True(!tracker.TryGet(999, out _), "不存在的 Id 应返回 false");

            tracker.MarkLegalTeleport(7, 42f);
            Eq(42f, a.LastLegalTeleportTime, "MarkLegalTeleport 应写入时间戳");

            tracker.Forget(7);
            True(!tracker.TryGet(7, out _), "Forget 后应查不到");

            // Clear 清空全部
            tracker.GetOrCreate(1, "X", 0f);
            tracker.GetOrCreate(2, "Y", 0f);
            tracker.Clear();
            Eq(0, tracker.Tracks.Count, "Clear 后应为空");
        }

        // ================= Violation =================

        private static void TestViolationWeight()
        {
            Eq(0.10f, Make(Severity.Low).BaseWeight, "Low 权重");
            Eq(0.30f, Make(Severity.Medium).BaseWeight, "Medium 权重");
            Eq(0.65f, Make(Severity.High).BaseWeight, "High 权重");
            Eq(0.90f, Make(Severity.Critical).BaseWeight, "Critical 权重");

            // Critical 刻意不到 1.0：避免单条证据一票否决
            True(Make(Severity.Critical).BaseWeight < 1.0f, "Critical 权重不应达到 1.0");

            // 空 metrics 不应为 null
            var v = Make(Severity.High);
            True(v.Metrics != null, "Metrics 不应为 null");
            Eq(0, v.Metrics.Count, "未传 metrics 时应为空集合");

            // 玩家名兜底
            var noName = new Violation(ViolationKind.Teleport, Severity.High, 1, null, 0f, "d");
            True(noName.PlayerName == "?", "玩家名为 null 时应兜底为 ?");
        }

        private static Violation Make(Severity s) =>
            new Violation(ViolationKind.Teleport, s, 1, "P", 0f, "detail");

        // ================= PlayerVerdict =================

        private static void TestEvaluateLevel()
        {
            var v = new PlayerVerdict { PlayerId = 1, Name = "P" };

            Eq(RiskLevel.Normal, v.EvaluateLevel(), "零命中应为 Normal");

            // 仅 High 命中 → Suspicious
            v.AddEvidence(Make(Severity.High));
            Eq(RiskLevel.Suspicious, v.EvaluateLevel(), "非确定性命中应为 Suspicious");

            // 出现 Critical → HighRisk
            v.AddEvidence(Make(Severity.Critical));
            Eq(RiskLevel.HighRisk, v.EvaluateLevel(), "有 Critical 应为 HighRisk");

            // AI 确认 → Confirmed（优先级高于 Critical）
            v.AiConfirmed = true;
            Eq(RiskLevel.Confirmed, v.EvaluateLevel(), "AI 确认应为 Confirmed 且优先于 HighRisk");

            // AI 取消确认后回到 HighRisk
            v.AiConfirmed = false;
            Eq(RiskLevel.HighRisk, v.EvaluateLevel(), "取消 AI 确认后应回落到 HighRisk");
        }

        private static void TestAddEvidence()
        {
            var v = new PlayerVerdict { PlayerId = 1, Name = "P" };
            Eq(0, v.EvidenceCount, "初始命中数为 0");
            Eq(0, v.CriticalCount, "初始 Critical 数为 0");

            v.AddEvidence(Make(Severity.Low));
            v.AddEvidence(Make(Severity.Medium));
            Eq(2, v.EvidenceCount, "两次普通命中后应为 2");
            Eq(0, v.CriticalCount, "非 Critical 不应计入 CriticalCount");

            v.AddEvidence(Make(Severity.Critical));
            Eq(3, v.EvidenceCount, "再加一条应为 3");
            Eq(1, v.CriticalCount, "Critical 应计入 CriticalCount");

            // null 不应崩、不应计数
            v.AddEvidence(null);
            Eq(3, v.EvidenceCount, "null 证据不应计数");

            Eq(3, v.Evidence.Count, "证据链应保留 3 条（供申诉说明）");
        }

        private static void TestVerdictReset()
        {
            var v = new PlayerVerdict { PlayerId = 1, Name = "P" };
            v.AddEvidence(Make(Severity.Critical));
            v.Warned = true;
            v.KickIssued = true;
            v.BanRequested = true;
            v.LastNotifiedLevel = RiskLevel.HighRisk;

            v.Reset();
            Eq(RiskLevel.Normal, v.LastNotifiedLevel, "Reset 后允许重新升级通知");

            Eq(0, v.EvidenceCount, "Reset 后命中数应清零");
            Eq(0, v.CriticalCount, "Reset 后 Critical 数应清零");
            True(!v.Warned, "Reset 后 Warned 应清除");
            True(!v.KickIssued, "Reset 后 KickIssued 应清除");
            True(!v.BanRequested, "Reset 后 BanRequested 应清除");
            Eq(0, v.Evidence.Count, "Reset 后证据链应清空");
            Eq(RiskLevel.Normal, v.EvaluateLevel(), "Reset 后判定应回到 Normal");
        }
    }
}
