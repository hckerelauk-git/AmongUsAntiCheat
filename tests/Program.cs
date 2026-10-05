using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>
        /// 去掉注释行再比对。
        ///
        /// 有些断言是「不许出现某种写法」，而**注释里正好在解释这种写法为什么不能用** ——
        /// 不剥注释就会把「解释坏写法」判成「用了坏写法」。这个坑刚踩过一次。
        /// </summary>
        private static string StripComments(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            foreach (var line in source.Split('\n'))
            {
                var t = line.TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) ||
                    t.StartsWith("/*", StringComparison.Ordinal) ||
                    t.StartsWith("*", StringComparison.Ordinal))
                    continue;
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

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
            Run("UI：行高统一为紧凑高度", () => Eq(46f, SettingsLayout.RowHeight(false), "行高"));
            Run("UI：分组标题占位为正", () => True(SettingsLayout.SectionCaptionHeight > 0f, "分组标题高度"));
            Run("UI：短页无滚动", () => Eq(0f, SettingsLayout.MaxScroll(100f, 300f), "短页"));
            Run("UI：长页末行可达", () => Eq(200f, SettingsLayout.MaxScroll(500f, 300f), "长页"));
            Run("UI：行为子页全部选项唯一覆盖", TestSettingsGroups);
            Run("UI：子标签表必须自洽且覆盖全部分组", TestSubTabCoverage);

            // ---------- 封禁名单 ----------
            Run("封禁名单：好友码格式校验", () =>
            {
                True(BanListDb.IsValidFriendCode("sloehind#4553"), "标准格式必须通过");
                True(BanListDb.IsValidFriendCode("a#0000"), "最短名字也要通过");
                True(!BanListDb.IsValidFriendCode("seniorhive8445"), "没有 # 的不是好友码");
                True(!BanListDb.IsValidFriendCode("#4553"), "# 前面必须有名字");
                True(!BanListDb.IsValidFriendCode("abc#455"), "# 后必须正好四位");
                True(!BanListDb.IsValidFriendCode("abc#45533"), "五位也不行");
                True(!BanListDb.IsValidFriendCode("abc#45a3"), "# 后必须是数字");
                True(!BanListDb.IsValidFriendCode(""), "空串不算");
            });

            Run("封禁名单：内置条目必须可定位", () =>
            {
                True(BanListDb.BuiltIn.Count > 0, "内置名单不能为空");
                foreach (var e in BanListDb.BuiltIn)
                {
                    True(!string.IsNullOrWhiteSpace(e.Name), "每条都要有名字");
                    True(!string.IsNullOrWhiteSpace(e.Reason), "每条都要写清楚为什么：" + e.Name);
                    // 写了好友码就必须是合法格式 —— 写错一位就永远匹配不上，
                    // 而且现场表现为「功能静默失效」，最难查
                    if (!string.IsNullOrEmpty(e.Code))
                        True(BanListDb.IsValidFriendCode(e.Code), "内置好友码格式必须合法：" + e.Code);
                }
            });

            Run("封禁名单：按好友码命中，改名也甩不掉", () =>
            {
                True(BanListDb.Check("sloehind#4553", "", "随便改的名字", null, out var hit), "必须命中");
                True(hit.Name == "sloehind", "命中的条目应为 sloehind，实际 " + hit.Name);
                True(BanListDb.Check("SLOEHIND#4553", "", "x", null, out _), "大小写不敏感");
                True(BanListDb.Check("linkfair#3286", "", "x", null, out _), "第二条也要命中");
            });

            Run("封禁名单：按平台 ID 命中", () =>
            {
                True(BanListDb.Check("", "seniorhive8445", "x", null, out var hit), "平台 ID 命中");
                True(hit.Name == "B站星函星辰", "命中的条目应为 B站星函星辰，实际 " + hit.Name);
            });

            Run("封禁名单：有 ID 的条目绝不按名字匹配", () =>
            {
                // 「黑龙」有好友码，就只认码 —— 否则「黑龙王」「黑龙丶」全被误伤
                True(!BanListDb.Check("", "", "黑龙", null, out _), "有好友码就不许按名字命中");
                True(!BanListDb.Check("", "", "黑龙王", null, out _), "更不许模糊命中");
                True(!BanListDb.Check("", "", "sloehind", null, out _), "名字对但没码，不命中");
                // 「鸟（繁体）」没有 ID，只能按名字
                True(BanListDb.Check("", "", "鸟（繁体）", null, out _), "无 ID 条目按名字命中");
                True(BanListDb.Check("", "", "鸟(繁体)", null, out _), "全角半角括号必须等价");
                True(!BanListDb.Check("", "", "小鸟", null, out _), "不能宽到误伤「小鸟」");
            });

            Run("封禁名单：自定义条目解析", () =>
            {
                var list = BanListDb.Parse("张三|zhangsan#1234||炸房;李四||puid-abc|刷屏");
                Eq(2, list.Count, "两条");
                True(list[0].Code == "zhangsan#1234", "好友码应为 zhangsan#1234，实际 " + list[0].Code);
                True(list[1].Puid == "puid-abc", "平台 ID 应为 puid-abc，实际 " + list[1].Puid);
                True(BanListDb.Check("zhangsan#1234", "", "换了名字", list, out _), "自定义条目要生效");
                Eq(0, BanListDb.Parse("").Count, "空串解析出空表");
                Eq(0, BanListDb.Parse(";;;;").Count, "全分隔符也解析出空表");
            });

            Run("封禁名单：序列化往返不丢信息", () =>            {
                var list = BanListDb.Parse("张三|zhangsan#1234|puid1|炸房");
                var round = BanListDb.Parse(BanListDb.Format(list));
                Eq(1, round.Count, "条数不变");
                True(round[0].Code == "zhangsan#1234", "好友码往返后变了：" + round[0].Code);
                True(round[0].Puid == "puid1", "平台 ID 往返后变了：" + round[0].Puid);
                True(round[0].Reason == "炸房", "备注往返后变了：" + round[0].Reason);
            });

            Run("封禁名单：名字里的分隔符不得破坏整份名单", () =>
            {
                // 备注里带 | 或 ; 会把后面的条目串味 —— 必须被替换掉
                var list = new List<BanEntry>
                {
                    new BanEntry { Name = "张三", Code = "zhangsan#1234", Reason = "炸房|很恶劣;还骂人" },
                };
                var round = BanListDb.Parse(BanListDb.Format(list));
                Eq(1, round.Count, "分隔符必须被清理，不能裂成多条");
                True(round[0].Code == "zhangsan#1234", "好友码要保住，实际 " + round[0].Code);
            });

            Run("封禁名单：接入运行时的硬性约束", () =>
            {                var runtime = System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/AntiCheatRuntime.cs")));

                // 名单里有「鸟（繁体）」这种只有名字的条目，不跳过自己就可能把自己踢了
                True(runtime.Contains("GameBridge.GetLocalPlayer()) continue"), "必须跳过本地玩家");
                // 检查每秒跑一次，没有去重表会每秒提交一条重复证据，通知直接刷屏
                True(runtime.Contains("BanReported.Contains(id)"), "必须按玩家去重");
                True(runtime.Contains("BanReported.Clear()"), "去重表必须每局清空，否则跨局残留");
                True(runtime.Contains("Config.EnableBanList.Value"), "必须受开关控制");
                // 走现有证据链，不另开一套处置逻辑
                True(runtime.Contains("ViolationKind.BannedPlayer") && runtime.Contains("Severity.Critical"),
                    "命中必须记为确定级证据，处置沿用现有设置");
            });

            // ---------- 在线封禁名单 ----------
            Run("在线名单：解析端点返回的真实结构", () =>
            {
                // 样本取自 https://api2.elauk.top/acban 的实际返回，
                // 用来把接口契约锁住 —— 端点改字段名时这里会先失败。
                const string sample = "{\"updatedAt\":1791172311,\"count\":3,\"bans\":["
                    + "{\"name\":\"示例玩家 Gamma\",\"friendCode\":\"CCCC3333\","
                    + "\"puid\":\"0000000000000000000000000000c3\",\"reason\":\"崩溃攻击\","
                    + "\"source\":\"ACE\",\"bannedAt\":1790955660,\"evidence\":\"\"},"
                    + "{\"name\":\"示例玩家 Beta\",\"friendCode\":\"BBBB2222\","
                    + "\"puid\":\"0000000000000000000000000000b2\",\"reason\":\"透视插件\","
                    + "\"source\":\"Amethyst\",\"bannedAt\":1790766120,\"evidence\":\"\"}]}";

                True(BanListRemote.Parse(sample, out var list, out var updatedAt, out var err),
                    "真实结构必须能解析，错误：" + err);
                Eq(2, list.Count, "条数");
                Eq(1791172311L, updatedAt, "数据时间");
                True(list[0].Name == "示例玩家 Gamma", "名字，实际 " + list[0].Name);
                True(list[0].Code == "CCCC3333", "好友码，实际 " + list[0].Code);
                True(list[0].Puid == "0000000000000000000000000000c3", "平台 ID");
                True(list[0].Reason.Contains("崩溃攻击") && list[0].Reason.Contains("ACE"),
                    "理由要带上来源，实际 " + list[0].Reason);
            });

            Run("在线名单：坏数据不得污染整份名单", () =>
            {
                // 单条脏数据只跳过它自己，不能连累其他条目
                const string mixed = "{\"bans\":["
                    + "{\"name\":\"好的\",\"friendCode\":\"GOOD1234\"},"
                    + "{\"name\":\"没有标识的\"},"
                    + "{\"name\":\"也是好的\",\"puid\":\"puid-xyz\"}]}";
                True(BanListRemote.Parse(mixed, out var list, out _, out _), "应解析成功");
                Eq(2, list.Count, "缺标识的条目要被跳过");

                // 整体结构不对 → 整批丢弃，绝不让坏数据覆盖已有名单
                True(!BanListRemote.Parse("", out _, out _, out _), "空串要失败");
                True(!BanListRemote.Parse("not json", out _, out _, out _), "非 JSON 要失败");
                True(!BanListRemote.Parse("{\"count\":3}", out _, out _, out _), "缺 bans 要失败");
                True(!BanListRemote.Parse("[]", out _, out _, out _), "顶层不是对象要失败");
                True(!BanListRemote.Parse("{\"bans\":[{\"name\":\"无标识\"}]}", out _, out _, out _),
                    "全部条目都不可用时视为失败，避免把已有名单清空");
            });

            Run("封禁名单：游戏内一键添加", () =>
            {
                // 抓到了却要手抄好友码去改配置文件，等于没抓到。
                var added = BanListDb.AppendEntry("", "作弊者", "abc#1234", "", "手动封禁");
                True(added.Contains("abc#1234"), "应写入好友码，实际 " + added);
                True(added.Contains("作弊者"), "应写入名字");
                // 注意：必须把解析后的条目传进去。传 null 只会查内置名单，
                // 而这里要验证的是「刚加进去的条目生效了」。
                var parsed = BanListDb.Parse(added);
                True(BanListDb.Check("abc#1234", "", "换了名字", parsed, out _), "加完必须立刻能命中");

                // 重复添加不生效 —— 否则配置串越加越长，用户还以为没生效
                var again = BanListDb.AppendEntry(added, "作弊者", "ABC#1234", "", "重复");
                True(again == added, "同标识不得重复添加（大小写不敏感）");

                // 没有可匹配标识时原样返回：那种条目本来就会被忽略
                True(BanListDb.AppendEntry("", "只有名字", "", "", "x") == "",
                    "无好友码且无平台 ID 时不得写入");

                // 追加不能破坏已有条目
                var two = BanListDb.AppendEntry(added, "第二个", "", "puid-xyz", "手动封禁");
                Eq(2, BanListDb.Parse(two).Count, "两条都要在");
                var twoParsed = BanListDb.Parse(two);
                True(BanListDb.Check("abc#1234", "", "x", twoParsed, out _), "旧条目仍要命中");
                True(BanListDb.Check("", "puid-xyz", "x", twoParsed, out _), "新条目要命中");
            });

            Run("在线名单：好友码两种表示都要能匹配", () =>
            {
                var remote = new List<BanEntry>
                {
                    new BanEntry { Name = "端点条目", Code = "CCCC3333", Reason = "在线" },
                };

                // 游戏内 FriendCode 可能带名字前缀
                True(BanListDb.Check("玩家名#CCCC3333", "", "x", null, remote, out _),
                    "带前缀的好友码必须命中");
                True(BanListDb.Check("CCCC3333", "", "x", null, remote, out _),
                    "纯码必须命中");

                // 但短码不做包含匹配 —— 否则会退化成子串碰撞
                var shortCode = new List<BanEntry> { new BanEntry { Name = "短码", Code = "1234" } };
                True(!BanListDb.Check("玩家名#1234", "", "x", null, shortCode, out _),
                    "短码不得做包含匹配，宁可漏判也不误判");

                // 在线名单优先级最高，命中时应报它而不是内置条目
                True(BanListDb.Check("CCCC3333", "", "x", null, remote, out var hit), "应命中");
                True(hit.Name == "端点条目", "应报在线名单条目，实际 " + hit.Name);
            });

            Run("主菜单：必须隐藏灰色遮罩与左侧分隔线", () =>
            {
                var art = StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/UI/MainMenuArt.cs"))));

                // MainUI/Tint 是覆盖右半屏的灰色半透明遮罩，子菜单打开时游戏会启用它。
                // 定位它踩过坑：早期用 sprite.bounds.size × scale 算尺寸，
                // 对九宫格精灵会算成 1.0x1.0，正好被「只列大尺寸渲染器」的过滤条件挡掉。
                True(art.Contains("\"MainUI/Tint\""), "必须隐藏 MainUI/Tint（右侧灰色遮罩）");
                // 高度为 0 的分隔线：一条灰线，用户明确要求去掉
                True(art.Contains("Main Buttons/Divider"), "必须隐藏左侧按钮区的分隔线");
                // 四个子菜单各有一条，只关一条的话切到别的子菜单又会冒出来
                foreach (var menu in new[] { "GameModeButtons", "OnlineButtons", "EnterCodeButtons", "AccountButtons" })
                    True(art.Contains(menu + "/Divider"), "必须隐藏 " + menu + " 的分隔线");

                // 只关 enabled 会有一帧闪烁：游戏在自己的 Update 里把它重新启用，
                // 而帧驱动按帧去重，谁先跑到算谁的。必须同时把 alpha 清零。
                True(art.Contains("sr.color = Color.clear"), "必须同时把 alpha 清零，否则会闪一帧");
                True(art.Contains("ClearedRenderers.Contains(sr)"), "清零的渲染器要纳入每帧重申");
            });

            Run("记录：命中必须带上可封禁的身份（好友码 / 平台 ID）", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));

                // 现场问题：日志里只有「777(6)」，玩家号每局重新分配 ——
                // 事后根本对不上人，抓到了也封不了。
                var engine = StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/VerdictEngine.cs")));
                True(engine.Contains("DescribeIdentity"), "VerdictEngine 必须能取到身份");
                True(engine.Contains("IdentityOf(verdict.PlayerId)"), "命中日志必须带上身份");

                var history = StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/HistoryLog.cs")));
                True(history.Contains("string identity = null"), "落盘接口必须接受身份");
                True(history.Contains("{identity} 命中"), "CheatHistory 必须写入身份");

                var runtime = StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/AntiCheatRuntime.cs")));
                True(runtime.Contains("GetFriendCode(p)") && runtime.Contains("GetPuid(p)"),
                    "身份必须同时取好友码与平台 ID");
                // 「他开的是什么挂」—— 模组指纹要一起记下来，否则只有个名字没法判断
                True(runtime.Contains("ModFingerprint.Of(id)"), "身份里必须带上模组指纹");
            });

            Run("配置：迁移表的目标键必须真实存在", () =>
            {
                // 踩过的坑：键名改了，但迁移表还指向旧名 ——
                // 老配置会迁移到一个不存在的键，然后被当垃圾删掉，用户的设置静默丢失。
                // 这类错误编译能过、测试也能过，只能靠全表校验兜住。
                var cfg = StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/Config/AntiCheatConfig.cs"))));

                var consts = new Dictionary<string, string>();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    cfg, "const string ([A-Z]) = \"([^\"]+)\";"))
                    consts[m.Groups[1].Value] = m.Groups[2].Value;

                var bindings = new HashSet<string>();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    cfg, "cfg\\.Bind\\(([A-Z]),\\s*\"([^\"]+)\""))
                    bindings.Add(consts[m.Groups[1].Value] + "/" + m.Groups[2].Value);

                var checkedCount = 0;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    cfg, "\\(\"([^\"]+)\", \"([^\"]+)\", \"([^\"]+)\", \"([^\"]+)\"\\),"))
                {
                    var target = m.Groups[3].Value + "/" + m.Groups[4].Value;
                    checkedCount++;
                    True(bindings.Contains(target),
                        "迁移目标键必须存在（否则用户设置会静默丢失）：" + target);
                }

                True(checkedCount > 50, "迁移表应被完整校验，实际校验 " + checkedCount + " 条");
            });

            Run("标记：判定结果必须标在头顶，不能只写日志", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
                var presence = StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/AcePresence.cs")));
                var tags = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/PresenceTags.cs"));

                // 打游戏的时候没人会去翻日志 —— 当场唯一能看见的告警就是名字标记。
                True(tags.Contains("HighRiskMark") && tags.Contains("SuspiciousMark"),
                    "必须提供高危 / 可疑标记");
                True(presence.Contains("RiskOf(id)"), "标记必须读取判定结果");
                True(presence.Contains("BuildTags(isAce, isAmethyst, id, risk)"), "风险必须传进标记");
                True(presence.Contains("PresenceTags.HighRiskNameHex"), "高危名字必须变色");

                // 被判定过的玩家即使关掉「标记非模组玩家」也要标出来
                True(presence.Contains("isVanilla && risk == RiskLevel.Normal"),
                    "判定过的玩家不得被「非模组玩家标记」开关挡掉");
                // 否则会出现「原本玩家 ⛔高危」这种自相矛盾的标记
                True(presence.Contains("parts.Count == 0 && risk == RiskLevel.Normal"),
                    "已有风险标记时不得再补「原本玩家」");
            });

            Run("RPC 洪水：加载/大厅阶段必须有更宽松的阈值", () =>
            {
                var guard = StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/Core/Rpc/RpcFloodGuard.cs"))));

                // 现场：三人同时在「加载/大厅」被判 61 个 RPC（阈值 60），
                // 超出只有 1~4 个，而且三人速率与类型完全相同 —— 那是模组的周期性广播，
                // 不是洪水攻击。大厅阶段本来就有大量合法的位置/外观/准备状态同步。
                True(guard.Contains("(loading ? 2 : 1)"), "加载/大厅阶段阈值必须放宽");
            });

            Run("判定：同一规则重复命中必须升级为高危", () =>
            {
                // 实测：一个人连续超速 8 次、最高 1.77 倍速，却因为 SpeedHack 的严重度
                // 是 High 而不是 Critical，永远停在「可疑」，升不到高危。
                var v = new PlayerVerdict { PlayerId = 1, Name = "测试" };

                for (var i = 1; i < PlayerVerdict.RepeatConfirmCount; i++)
                {
                    v.AddEvidence(new Violation(ViolationKind.SpeedHack, Severity.High, 1, "测试", i, "超速"));
                    True(v.EvaluateLevel() == RiskLevel.Suspicious,
                        "重复 " + i + " 次（未达阈值）应仍为可疑");
                }

                v.AddEvidence(new Violation(ViolationKind.SpeedHack, Severity.High, 1, "测试", 9f, "超速"));
                True(v.EvaluateLevel() == RiskLevel.HighRisk,
                    "同一条规则重复命中达到阈值必须升级为高危");

                // 不同规则各命中一次不能触发 —— 那才叫分数累计，本项目不做
                var spread = new PlayerVerdict { PlayerId = 2, Name = "分散" };
                var kinds = new[] { ViolationKind.Teleport, ViolationKind.SpeedHack, ViolationKind.ChatFlood,
                                    ViolationKind.IllegalVent, ViolationKind.TaskTooFast, ViolationKind.WallClip };
                for (var i = 0; i < kinds.Length; i++)
                    spread.AddEvidence(new Violation(kinds[i], Severity.High, 2, "分散", i, "各一条"));
                True(spread.EvaluateLevel() == RiskLevel.Suspicious,
                    "不同规则各命中一次不得升级（那是分数累计）");

                v.Reset();
                True(v.EvaluateLevel() == RiskLevel.Normal, "Reset 必须清空重复计数");
            });

            Run("采样：场景级传送必须整批豁免", () =>
            {
                var runtime = StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/AntiCheatRuntime.cs"))));

                // 会议开始会把所有人一起传送到会议桌，而且比 inMeeting 状态早一帧 ——
                // 实测诊断：所有状态标志为 0，紧接着下一行才是「会议开始」。
                // 逐人判定看不出这种情况，必须在批次层面统计。
                True(runtime.Contains("bigMovers"), "必须统计本帧大位移人数");
                True(runtime.Contains("bigMovers >= 2"), "两人及以上同时大位移视为场景传送");
                True(runtime.Contains("t.LastLegalTeleportTime = now"), "场景传送必须整批开豁免窗口");
            });

            Run("主菜单：顶栏透明化不得被其它逻辑短路", () =>
            {
                var art = StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/UI/MainMenuArt.cs"))));

                // 回归：ClearTopBar 曾用 `ClearedRenderers.Count >= 1` 当「已处理」守卫，
                // 而隐藏原版图层也往同一个列表里加、且先执行 ——
                // 列表立刻被填满，守卫命中，顶栏再也不会被透明化。
                True(!art.Contains("ClearedRenderers.Count >= TransparentSpritePaths.Length"),
                    "不得用共用列表的长度当「已处理」守卫");
                True(art.Contains("_topBarCleared"), "顶栏必须有独立的完成标志");
                True(art.Contains("if (_topBarCleared) return;"), "守卫必须读独立标志");
                True(art.Contains("_topBarCleared = false;"), "菜单重建时必须重置，否则换局后不再生效");
            });

            Run("启动动画：必须使用内嵌图标", () =>
            {
                var splash = StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/UI/DesktopSplash.cs"))));

                // 窗口是纯 GDI 的，GDI 画不了 PNG，必须借 GDI+ 解一次
                True(splash.Contains("GdiplusStartup"), "必须初始化 GDI+");
                True(splash.Contains("GdipCreateBitmapFromFile"), "必须从文件加载 PNG");
                True(splash.Contains("GdipDrawImageRectI"), "必须把图标绘制到徽标位置");
                True(splash.Contains("ApexCheatEnder.Icon.png"), "必须读取内嵌图标资源");
                True(splash.Contains("DrawIconInto"), "必须优先画图标");
                // 任何一步失败都要能回退，不能让动画整体失效
                True(splash.Contains("GdiplusShutdown") && splash.Contains("GdipDisposeImage"),
                    "GDI+ 资源必须释放，否则句柄泄漏");
            });

            Run("在线名单：必须直连，不走系统代理", () =>
            {
                var remote = StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/Core/BanListRemote.cs"))));

                // 实测走代理会超时：端点在国内（腾讯云），经本地代理绕一圈反而连不上，
                // 769 字节的响应 10 秒超时。
                True(remote.Contains("UseProxy = false"), "必须显式关闭代理");
            });

            Run("在线名单：必须在进入对局之前就开始拉取", () =>
            {
                // 换行符先归一化 —— 文件在 Windows 下是 CRLF，直接找 "\n" 会失配
                var runtime = StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/AntiCheatRuntime.cs"))))
                    .Replace("\r\n", "\n");

                // 放在 CheckBanList 里会导致只有进对局才拉取，而玩家通常先进大厅再进房，
                // 名单永远来不及准备好 —— 表现为「功能未生效」，日志里却一条错都没有。
                var iTick = runtime.IndexOf("BanListRemote.Tick(now,", StringComparison.Ordinal);
                var iReturn = runtime.IndexOf("if (!inGame)\n", StringComparison.Ordinal);
                True(iTick >= 0, "必须调用 BanListRemote.Tick");
                True(iReturn > 0, "应能定位「不在对局中」的提前返回");
                True(iTick < iReturn, "拉取必须发生在「不在对局中」提前返回之前");

                // 日志钩子必须在任何状态下都会安装，否则失败信息完全不可见
                var iHook = runtime.IndexOf("BanListRemote.LogInfo = m =>", StringComparison.Ordinal);
                True(iHook >= 0 && iHook < iReturn, "日志钩子必须在提前返回之前安装");
            });

            // ---------- 性能：热路径不得产生垃圾 ----------
            Run("性能：每个 RPC / 每个网络包的补丁不得用 object[] __args", () =>
            {
                // Harmony 的 object[] __args 会让**每次调用**都新建数组并装箱所有参数。
                // 挂在 HandleRpc / HandleGameData 这种每包都跑的方法上，
                // 就是持续不断的垃圾，攒够了触发 GC —— 现场表现就是周期性掉帧。
                // 必须用 [HarmonyArgument(n)] 强类型参数，直接读 IL 实参，零分配。
                var hot = new[]
                {
                    "Patches/AcePresencePatch.cs",
                    "Patches/AmethystPresencePatch.cs",
                    "Patches/RpcGuardPatches.cs",
                    "Patches/GameplayPatches.cs",
                    "Patches/PlayerActionPatches.cs",
                };

                foreach (var file in hot)
                {
                    var src = StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src", file))));
                    True(!src.Contains("object[] __args"),
                        file + " 不得用 object[] __args（每次调用都建数组 + 装箱）");
                    True(src.Contains("[HarmonyArgument("),
                        file + " 必须用 [HarmonyArgument(n)] 强类型参数");
                }
            });

            Run("性能：超大数据包检测不得再用反射读 Length", () =>
            {
                var gameplay = StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/Patches/GameplayPatches.cs"))));

                // HandleGameData 每个网络包都跑，而这项检测默认是开的。
                // 原来那里是 GetType().Name 字符串比较 + PropertyInfo.GetValue 反射取值。
                True(!gameplay.Contains("PropertyInfo"), "不得再用反射读 MessageReader.Length");
                True(!gameplay.Contains("_lengthProp"), "反射缓存字段应已移除");
                True(gameplay.Contains("reader.Length"), "必须直接读 MessageReader.Length");
            });

            Run("性能：名单检查每人每局只查一次", () =>
            {                var runtime = System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/AntiCheatRuntime.cs")));

                // 每秒给全房每人做一次字符串归一化 = 每秒几十次分配，纯浪费。
                // 好友码不会中途变，查过就记下来。
                True(runtime.Contains("BanChecked"), "必须有「已查过」集合");
                True(runtime.Contains("BanChecked.Clear()"), "每局必须清空");
                True(runtime.Contains("BanChecked.Add(id)"), "必须在跳过判断之前标记已查");
            });

            Run("性能：UI 不得每帧无条件写 RectTransform", () =>
            {                // Unity 里给 RectTransform 赋值**一定标脏**，进而触发整个 Canvas 重新生成网格。
                // 设置页有几百个元素，每帧重建一次 = 「UI 卡成幻灯片、游戏却一点不卡」。
                // 所以每一处写入都必须先比较、没变就跳过。
                string Read(string rel) => StripComments(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src", rel))));

                var settings = Read("UI/SettingsWindow.cs");
                True(settings.Contains("_appliedScroll") && settings.Contains("Mathf.Approximately(_scrollOffset, _appliedScroll)"),
                    "滚动必须先比较再写");
                True(settings.Contains("_appliedWindowScale") && settings.Contains("Mathf.Approximately(scale, _appliedWindowScale)"),
                    "窗口缩放必须先比较再写");
                True(settings.Contains("(target - p).sqrMagnitude"),
                    "窗口位置必须先比较再写");
                True(settings.Contains("_footerFlashing"),
                    "页脚文案必须先比较状态再写");

                var notice = Read("UI/NotificationPanel.cs");
                True(notice.Contains("sqrMagnitude"),
                    "通知槽位重排必须先比较再写");

                var chat = Read("UI/ChatAbuseNotice.cs");
                True(chat.Contains("_root != null && _root.activeSelf"),
                    "隐藏前必须先判断当前是否已隐藏");
            });
            Run("UI：数字行滑条占独立右侧区域", () =>
            {
                var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    AppContext.BaseDirectory, "../../../../src/UI/SettingsWindow.cs"));
                var ui = System.IO.File.ReadAllText(path);
                True(ui.Contains("SliderHit"), "数字行必须有独立滑条区域");
                // 说明文字不再常驻行内，否则行高又会被撑回去
                // 说明必须常驻在行内。之前挪到底部栏、只在悬停时显示，
                // 结果是「不把鼠标挨个划一遍就不知道每项是干什么的」。
                True(ui.Contains("\"RowDesc\""), "说明必须常驻在行内，不能只靠悬停");
                True(ui.Contains("hasHint ? 62f : 46f") || ui.Contains("RowHeight(hasHint)"),
                    "带说明的行必须留出第二行高度");
                True(ui.Contains("UpdateHoverHint") && ui.Contains("_hintText"),
                    "必须把说明搬到悬停描述栏");
            });
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
            Run("拦截：接收端 RPC 守卫源码约束", TestRpcGuardSource);
            Run("任务：坐标不可信时不做绝对距离定罪", TestTaskPositionTrust);
            Run("扫描：不得把本插件判成作弊", TestScannerSelfExclusion);
            Run("互认：Amethyst 旁听边界", TestAmethystPresenceSource);
            Run("会议：入会传送不得判为会议期间走动", TestMeetingEnterGrace);
            Run("处置：手动踢人必须点击时重新校验目标", TestManualKickSafety);
            Run("设置窗口：可拖拽缩放且尺寸持久化", TestResizableSettingsWindow);
            Run("通知：不得只爆一次，也不得刷屏", TestNotificationFrequency);
            Run("破坏：必须确认是 UpdateSystem 才能归因", TestSabotageAttribution);
            Run("分辨率：异常判定与修复目标校验", TestResolutionPolicy);
            Run("图标：资源存在且已接入标题栏与通知", TestAppIconResource);
            Run("模组指纹：按自定义 RPC 识别，库里没有就显示 GUID 原文", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
                var db = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/ModFingerprintDb.cs"));
                // 原版 RpcCalls 最大 67（SpiritGuideMessage），超出即模组自定义
                True(db.Contains("VanillaRpcMax = 67"), "必须以原版 RpcCalls 上限为界");
                True(db.Contains("DisplayOf"), "必须支持 GUID→名字 查询");
                // 用户要求：库里没有就直接显示 GUID 原文，不能显示空白
                True(db.Contains("ByGuid.TryGetValue(guid, out var name) ? name : guid"),
                    "查不到 GUID 时必须回退显示 GUID 原文");
                // NitroAntiCheat 实测指纹
                True(db.Contains("com.anonymus.aum"), "必须收录 AUM");
                True(db.Contains("sicko.menu") && db.Contains("killnetwork"), "必须收录 SickoMenu / KillNetwork");

                var fp = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/ModFingerprint.cs"));
                True(fp.Contains("IsCustomCallId(callId)"), "原版范围内的 callId 必须立即放行（热路径）");
                True(fp.Contains("Announce"), "必须支持模组自报家门");
                True(fp.Contains("未知模组 #"), "认不出的自定义 ID 要按原始编号记下来");

                // 观测点必须挂在 HandleRpc 上，否则看不到别人的 RPC
                var patch = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Patches/GameplayPatches.cs"));
                True(patch.Contains("ModFingerprint.Observe"), "必须在 HandleRpc 的 Prefix 上观测");
            });

            Run("标记：所有玩家头上都要有身份标识", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
                var presence = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/AcePresence.cs"));
                // 非模组玩家也要打标
                True(presence.Contains("MarkVanillaPlayers"), "必须有「标记非模组玩家」开关");
                True(presence.Contains("PresenceTags.VanillaDefault"), "非模组玩家要有默认标记");
                // Amethyst 用户的 UID：协议只传 PlayerId + 版本，版本是唯一身份信息
                True(presence.Contains("AmethystPresence.GetVersion"), "Amethyst 用户必须带上版本号");
                True(presence.Contains("ModFingerprint.Of"), "标记必须由模组指纹驱动");
                // 自己不打标
                True(presence.Contains("local != null && GameBridge.GetPlayerId(local) == id"), "自己头上不打标");

                var cfg = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Config/AntiCheatConfig.cs"));
                True(cfg.Contains("标记非模组玩家") && cfg.Contains("非模组玩家标记写成什么"),
                    "两个配置项都要有");

                var tags = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/PresenceTags.cs"));
                True(tags.Contains("原版玩家"), "默认文案应为「原版玩家」");
                True(tags.Contains("LegacyVanillaTag"), "必须记录旧文案以便迁移");
            });

            Run("瞬移阈值：必须高于网络延迟校正的幅度", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
                var cfg = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Config/AntiCheatConfig.cs"));
                // 实测 4.79 单位的位移是延迟校正，不是作弊
                True(cfg.Contains("\"瞬移判定距离\", 8.0f"), "瞬移阈值默认必须抬到 8.0");
                True(cfg.Contains("AcceptableValueRange<float>(1f, 60f)"), "上限要够容纳真实瞬移");
                True(cfg.Contains("Math.Abs(TeleportMinDistance.Value - 4.5f)"), "必须有旧值迁移");
            });

            Run("骂人提示：引用/否定语境不提示，且不提示自己", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
                var policy = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/ChatAbuseNoticePolicy.cs"));
                True(policy.Contains("BenignPrefixes") && policy.Contains("HasBenignPrefix"),
                    "必须豁免引用/否定语境（中文无词边界，纯子串必然误报）");

                var notice = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/UI/ChatAbuseNotice.cs"));
                True(notice.Contains("fromSelf"), "自己发的消息不得提示");
            });

            Run("瞬移：落点不稳（网络抖动）不得判为瞬移", TestTeleportNeedsStableLanding);
            Run("击杀：采样窗口内存在合法距离时不得判超距", TestKillDistanceUsesWindowMinimum);
            Run("击杀：同一次击杀被上报两次不得判为冷却绕过", () =>
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "ace-kcd-" + Guid.NewGuid().ToString("N") + ".cfg");
                var cfg = new ApexCheatEnder.Config.AntiCheatConfig(new BepInEx.Configuration.ConfigFile(path, false));
                var analyzer = new BehaviorAnalyzer(cfg, null);

                PlayerTrack MakeKiller(int id)
                {
                    var t = new PlayerTrack(id, "凶手", 0f);
                    t.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f), IsImpostor = true, RoleKnown = true });
                    t.Push(new PlayerSnapshot { Time = 0.1f, Position = new GameVec2(0f, 0f), IsImpostor = true, RoleKnown = true });
                    return t;
                }
                PlayerTrack MakeVictim(int id)
                {
                    var t = new PlayerTrack(id, "目标", 0f);
                    t.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0.5f, 0f) });
                    t.Push(new PlayerSnapshot { Time = 0.1f, Position = new GameVec2(0.5f, 0f) });
                    return t;
                }

                // 第一次击杀：正常，不该报
                var k = MakeKiller(1);
                var v = MakeVictim(2);
                analyzer.AnalyzeKill(k, v, 1f, 5f, 30f, new List<Violation>());

                // 0.2 秒后「同凶手 + 同目标」再次出现 —— 同一次击杀被处理了两次。
                // 游戏里不可能杀同一个人两次（第二次时对方已经是尸体）。
                var dup = new List<Violation>();
                analyzer.AnalyzeKill(k, v, 1.2f, 5f, 30f, dup);
                True(!dup.Exists(x => x.Kind == ViolationKind.KillCooldownBypass),
                    "同凶手同目标极短间隔 = 同一次击杀，不得判冷却绕过");

                // 真作弊杀的是**不同**的人 —— 必须照样抓住
                var k2 = MakeKiller(3);
                analyzer.AnalyzeKill(k2, MakeVictim(4), 1f, 5f, 30f, new List<Violation>());
                var cheat = new List<Violation>();
                analyzer.AnalyzeKill(k2, MakeVictim(5), 1.2f, 5f, 30f, cheat);
                True(cheat.Exists(x => x.Kind == ViolationKind.KillCooldownBypass),
                    "不同目标在冷却内连杀必须判为绕过 —— 排重不能把真作弊一起漏掉");
            });
            Run("瞬移：必须豁免梯子与移动平台，不能只判管道", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
                var bridge = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/GameBridge.cs"));
                // 爬梯子、站飞艇移动平台都会产生合法大位移
                True(bridge.Contains("player.onLadder"), "必须豁免爬梯子");
                True(bridge.Contains("player.inMovingPlat"), "必须豁免移动平台");
                True(bridge.Contains("IsInSpecialMovement"), "必须提供统一判定");

                var analyzer = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/BehaviorAnalyzer.cs"));
                True(analyzer.Contains("cur.InVent || cur.InSpecialMovement"),
                    "瞬移/超速判定必须同时豁免这两种状态");
            });

            Run("标记：Amethyst 用户必须粉心 + 粉色名字，且旧配置能迁移", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
                var tags = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/PresenceTags.cs"));
                True(tags.Contains("💗AME用户💗"), "默认标记必须是粉心 💗");
                True(tags.Contains("LegacyAmethystTag"), "必须记录旧默认值以便迁移");

                var presence = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/AcePresence.cs"));
                True(presence.Contains("<color=#") && presence.Contains("AmethystNameHex"),
                    "Amethyst 用户名字必须上粉色");
                // 名字颜色由游戏每帧写，改 Graphic 颜色会被覆盖闪烁，必须用富文本
                True(!presence.Contains("nameText.color ="), "不得直接改 TextMeshPro 颜色，会被游戏覆盖");

                var cfg = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Config/AntiCheatConfig.cs"));
                True(cfg.Contains("LegacyAmethystTag"), "必须做一次性迁移，否则老配置还是紫心");
            });

            Run("击杀：角色未知时不得判「非内鬼击杀」", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
                var analyzer = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/BehaviorAnalyzer.cs"));
                // 角色未同步时 IsImpostor 恒为 false，不设守卫会误报开局阶段的正常击杀
                True(analyzer.Contains("killer.Current.RoleKnown && !killer.Current.IsImpostor"),
                    "击杀判定必须先确认角色已知");
            });

            Run("穿墙：必须五点采样 + 排除触发器 + 连续确认", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
                var bridge = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/GameBridge.cs"));
                // Ship 层不只含墙，还含桌子/控制台；只测中心会误伤贴边站立的玩家
                True(bridge.Contains("InsideAt(point, radius, 0f)") && bridge.Contains("InsideAt(point, -radius, 0f)")
                     && bridge.Contains("InsideAt(point, 0f, radius)") && bridge.Contains("InsideAt(point, 0f, -radius)"),
                    "必须中心加四周五点全部命中才算陷在墙里");
                True(bridge.Contains("!hit.isTrigger"), "必须排除触发器碰撞体（控制台/通风管不是墙）");

                var analyzer = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Core/BehaviorAnalyzer.cs"));
                True(analyzer.Contains("ConsecutiveWallStrikes >= 2"), "穿墙必须连续采样确认，单次擦边不算");
            });

            Run("UI：通知停留时长足够读完，且槽位够用", () =>
            {
                var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
                var cfg = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/Config/AntiCheatConfig.cs"));
                // 原来 5 秒，现场反馈"还没读完就没了"
                True(cfg.Contains("\"通知停留时长（秒）\", 12f"), "通知默认停留时长必须放宽到 12 秒");
                True(cfg.Contains("AcceptableValueRange<float>(2f, 60f)"), "上限必须提到 60 秒");

                var panel = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/UI/NotificationPanel.cs"));
                True(panel.Contains("SlotCount = 5"), "同时显示的通知槽位必须够多，否则长时长反而互相挤掉");
            });

            Run("UI：记录页必须给出「谁·几次·犯了什么」", () =>
            {
                var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    AppContext.BaseDirectory, "../../../../src/UI/SettingsWindow.cs"));
                var ui = System.IO.File.ReadAllText(path);
                var at = ui.IndexOf("BuildToolsPage", StringComparison.Ordinal);
                True(at > 0, "应能找到记录与工具页");
                var block = ui.Substring(at, ui.IndexOf("private static string RuleHintOf", at) - at);
                True(block.Contains("检测汇总"), "必须有检测汇总");
                True(block.Contains("v.Evidence.Count") && block.Contains("共 ") && block.Contains(" 次"),
                    "必须显示命中次数");
                True(block.Contains("counts[") && block.Contains("EvaluateLevel"),
                    "必须按规则聚合计数并显示风险等级");
                True(block.Contains("RuleHintOf"), "必须为每条规则给出中文说明（犯了什么）");
            });

            Run("UI：重建时必须清空内容区，否则旧行与新行重叠", () =>
            {
                var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    AppContext.BaseDirectory, "../../../../src/UI/SettingsWindow.cs"));
                var ui = System.IO.File.ReadAllText(path);
                // 行现在直接挂在 _contentArea 下，不清空就会重影（现场出过）
                True(ui.Contains("_contentArea.childCount") && ui.Contains("UnityEngine.Object.Destroy(child.gameObject)"),
                    "重建前必须销毁内容区的全部子物体");
            });
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
            Run("工具：分组编辑和历史映射", ToolsPageTests.Mapping);
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
            // 按组名断言，不写死组号 —— 重新划分子标签时不该再挂
            True(SettingsLayout.GroupNames[2][SettingsLayout.Group(2, 100)] == "当前命中玩家",
                "动态处置玩家应归入「当前命中玩家」分组，实际 "
                + SettingsLayout.GroupNames[2][SettingsLayout.Group(2, 100)]);
        }

        /// <summary>随机池里的全部下标。张数变了不用改测试，跟着 BackgroundCount 走。</summary>
        private static int[] AllBackgroundIndexes()
        {
            var all = new int[MenuArtSource.BackgroundCount];
            for (var i = 0; i < all.Length; i++) all[i] = i;
            return all;
        }

        /// <summary>
        /// 子标签表必须自洽。
        ///
        /// 最要命的一条是「每个分组都得被某个子标签覆盖」——
        /// 漏掉一个分组，那部分设置就**永远看不到**（切遍所有子标签都找不到），
        /// 而且不会报任何错。这类「东西还在但摸不到」的问题只能靠全表校验兜住。
        /// </summary>
        private static void TestSubTabCoverage()
        {
            for (var page = 0; page < SettingsLayout.GroupNames.Length; page++)
            {
                var defs = SettingsLayout.SubTabsFor(page);
                True(defs.Length >= 1, "第 " + page + " 页必须有子标签定义");

                var groupCount = SettingsLayout.GroupNames[page].Length;

                foreach (var d in defs)
                {
                    True(d.Groups != null && d.Groups.Length >= 1, d.Name + " 必须至少覆盖一个分组");
                    foreach (var g in d.Groups)
                        True(g >= 0 && g < groupCount,
                            "子标签「" + d.Name + "」引用了不存在的分组 " + g + "（本页只有 " + groupCount + " 个）");
                }

                // 覆盖性：每个分组都必须出现在至少一个子标签里
                for (var g = 0; g < groupCount; g++)
                {
                    var covered = false;
                    foreach (var d in defs)
                        if (System.Array.IndexOf(d.Groups, g) >= 0) { covered = true; break; }

                    True(covered, "第 " + page + " 页分组「" + SettingsLayout.GroupNames[page][g]
                        + "」没有被任何子标签覆盖 —— 这部分设置将无法看到");
                }
            }
        }

        private static void TestMenuArtInitialSelection()
        {
            Eq(5, MenuArtSource.BackgroundCount, "随机池应为五张图片");
            var seen = new HashSet<int>();
            var random = new Random(20261003);
            for (var i = 0; i < 100; i++)
            {
                var selected = MenuArtSource.SelectBackground(-1, random);
                True(selected >= 0 && selected < MenuArtSource.BackgroundCount, "选择必须落在候选范围内");
                seen.Add(selected);
            }
            True(seen.SetEquals(AllBackgroundIndexes()), "首次选择应覆盖全部候选图片");
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
                    True(next >= 0 && next < MenuArtSource.BackgroundCount, "选择必须落在候选范围内");
                    True(next != previous, "连续两次不能选择同一张图片");
                    seen.Add(next);
                    previous = next;
                }
                True(seen.SetEquals(AllBackgroundIndexes()), "连续选择应覆盖全部候选图片，而非固定交替");
            }
        }

        private static void TestMenuArtResourceFiles()
        {
            var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src/Resources"));
            foreach (var resource in MenuArtSource.PoolResources.Concat(new[] { MenuArtSource.FallbackResource }))
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
            // 中文没有词分隔符，嵌入句中的中文骂词必须能命中。
            // 旧规则对长度 ≤2 的中文词也要求边界，「前缀傻逼后缀」会被整体拒绝 ——
            // 那是最常见的骂法，拒绝掉等于词表作废。
            True(ChatAbuseNoticePolicy.Matches("前缀傻逼后缀", "傻逼"), "中文骂词嵌入句中必须命中");
            True(ChatAbuseNoticePolicy.Matches("你个智障玩意儿", "智障"), "中文骂词嵌入句中必须命中（2 字）");
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
            var expected = new HashSet<string>(MenuArtSource.PoolResources)
                { MenuArtSource.FallbackResource };
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

        /// <summary>
        /// 接收端拦截层的关键不变量。
        ///
        /// 这层直接依赖 Unity / IL2CPP，没法在纯逻辑测试里跑真实 RPC，
        /// 所以用源码约束守住三条「一旦破坏就是事故」的规则：
        /// 拿不到角色必须放行、只在房主生效、优先级必须低于 RpcContext。
        /// </summary>
        private static void TestRpcGuardSource()
        {
            var source = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src"));
            var guard = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "Patches/RpcGuardPatches.cs"));

            // 1. 必须用三态查询：拿不到角色信息时 TryGet* 返回 false，此时一律放行。
            //    若改成直接读 role.CanUseKillButton，角色未同步就会把内鬼的正常击杀砍掉。
            True(guard.Contains("TryGetCanKill") && guard.Contains("TryGetCanVent"),
                "必须用三态角色查询，禁止直接读角色字段");
            True(!guard.Contains("role.CanUseKillButton") && !guard.Contains("role.CanVent"),
                "不得绕过三态查询直接访问角色能力");

            // 2. 只在房主生效：非房主拦了只会造成各端表现不一致。
            True(guard.Contains("GameBridge.IsHost"), "拦截必须限定房主");

            // 3. 优先级必须低于 RpcContextPatch：Harmony 跳过后续 Prefix 时仍会执行
            //    所有 Postfix，若本 Prefix 抢在 Push 之前返回 false，就会把外层栈帧弹掉。
            True(guard.Contains("HarmonyPriority(Priority.Low)"), "优先级必须低于 RpcContext，保证 Push/Pop 配对");

            // 4. 只拦语义确定的两个 RPC，别的走 default 放行。
            True(guard.Contains("RpcCalls.MurderPlayer") && guard.Contains("RpcCalls.EnterVent"),
                "只拦角色明确不允许的击杀与钻管道");

            // 5. 默认必须放行：Prefix 的 catch 之后必须 return true。
            var prefixStart = guard.IndexOf("private static bool Prefix", StringComparison.Ordinal);
            var prefixEnd = guard.IndexOf("private static bool BlockImpossibleKill", StringComparison.Ordinal);
            True(prefixStart >= 0 && prefixEnd > prefixStart, "应能定位 Prefix 方法体");
            var prefix = guard.Substring(prefixStart, prefixEnd - prefixStart);
            var catchAt = prefix.LastIndexOf("catch", StringComparison.Ordinal);
            True(catchAt >= 0 && prefix.Substring(catchAt).Contains("return true;"),
                "Prefix 捕获异常后必须放行，不能拦错");
        }

        /// <summary>
        /// 任务点坐标不可信时，绝对距离判定必须停用。
        ///
        /// 好友现场日志：14 次「远程任务」命中的距离全部是同一个 5.82 ——
        /// 说明拿到的是 PlayerTask.transform.position（任务对象自身位置），
        /// 而不是玩家真正要站的任务交互点。用它做绝对距离判定必然误报。
        ///
        /// 相对位移判定（任务速度）比较同一玩家前后两次任务点，固定偏移相减时抵消，
        /// 因此必须保留 —— 这条测试同时守住「别一刀切把任务检测全删了」。
        /// </summary>
        /// <summary>
        /// 瞬移必须先确认落点稳定。
        ///
        /// 网络抖动会让位置跳过去、下一帧又跳回来（客户端拿到权威位置后自我纠正）；
        /// 真实瞬移的落点不会回去。现场出现过单帧 11.77 单位的跳变，就是一次重同步。
        /// </summary>
        private static void TestTeleportNeedsStableLanding()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "ace-tp-" + Guid.NewGuid().ToString("N") + ".cfg");
            var cfg = new ApexCheatEnder.Config.AntiCheatConfig(new BepInEx.Configuration.ConfigFile(path, false));
            var analyzer = new BehaviorAnalyzer(cfg, null);

            // --- 抖动：跳过去又跳回来 → 不得上报 ---
            var jitter = new PlayerTrack(1, "抖动", 0f);
            jitter.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f) });
            jitter.Push(new PlayerSnapshot { Time = 5f, Position = new GameVec2(0f, 0f) });     // 过开局宽限期
            jitter.Push(new PlayerSnapshot { Time = 5.1f, Position = new GameVec2(20f, 0f) });  // 单帧跳 20 单位

            var first = new List<Violation>();
            analyzer.AnalyzeMovement(jitter, 5.1f, 4f, 0f, first);
            True(!first.Exists(v => v.Kind == ViolationKind.Teleport),
                "检测到不可能位移的当帧不得立即上报，必须先确认落点");

            jitter.Push(new PlayerSnapshot { Time = 5.2f, Position = new GameVec2(0.05f, 0f) }); // 跳回原处
            var back = new List<Violation>();
            analyzer.AnalyzeMovement(jitter, 5.2f, 4f, 0f, back);
            True(!back.Exists(v => v.Kind == ViolationKind.Teleport),
                "位置跳回原处说明是网络抖动，不得判为瞬移");

            // --- 真瞬移：落点稳定 → 必须上报 ---
            var real = new PlayerTrack(2, "瞬移", 0f);
            real.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f) });
            real.Push(new PlayerSnapshot { Time = 5f, Position = new GameVec2(0f, 0f) });
            real.Push(new PlayerSnapshot { Time = 5.1f, Position = new GameVec2(20f, 0f) });
            analyzer.AnalyzeMovement(real, 5.1f, 4f, 0f, new List<Violation>());

            real.Push(new PlayerSnapshot { Time = 5.2f, Position = new GameVec2(20.1f, 0f) }); // 落点稳定
            var confirmed = new List<Violation>();
            analyzer.AnalyzeMovement(real, 5.2f, 4f, 0f, confirmed);
            True(confirmed.Exists(v => v.Kind == ViolationKind.Teleport),
                "落点稳定时必须判为瞬移，否则真作弊会漏掉");
        }

        /// <summary>
        /// 击杀距离必须取采样窗口内的最小值。
        ///
        /// killer 与 victim 的位置各来自最近一次采样，击杀 RPC 还带网络延迟；
        /// 只比「当前点对当前点」会把正常击杀算成超限。现场出现过 2.10 / 上限 1.75。
        /// </summary>
        private static void TestKillDistanceUsesWindowMinimum()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "ace-kill-" + Guid.NewGuid().ToString("N") + ".cfg");
            var cfg = new ApexCheatEnder.Config.AntiCheatConfig(new BepInEx.Configuration.ConfigFile(path, false));
            var analyzer = new BehaviorAnalyzer(cfg, null);

            // 双方都在移动：当前点相距 2.10（超限），但上一帧组合只有 1.50（限内）
            var killer = new PlayerTrack(1, "杀手", 0f);
            var victim = new PlayerTrack(2, "目标", 0f);
            killer.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f), IsImpostor = true, RoleKnown = true });
            killer.Push(new PlayerSnapshot { Time = 0.1f, Position = new GameVec2(2.1f, 0f), IsImpostor = true, RoleKnown = true });
            victim.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(1.5f, 0f) });
            victim.Push(new PlayerSnapshot { Time = 0.1f, Position = new GameVec2(0f, 0f) });

            var near = new List<Violation>();
            analyzer.AnalyzeKill(killer, victim, 0.1f, 1.0f, 20f, near);
            True(!near.Exists(v => v.Kind == ViolationKind.KillTooFar),
                "采样窗口内存在合法距离时不得判超距击杀");

            // 真远距离击杀：所有时刻组合都超限 → 必须报
            var far = new PlayerTrack(3, "远杀", 0f);
            var farVictim = new PlayerTrack(4, "目标二", 0f);
            far.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f), IsImpostor = true, RoleKnown = true });
            far.Push(new PlayerSnapshot { Time = 0.1f, Position = new GameVec2(0.1f, 0f), IsImpostor = true, RoleKnown = true });
            farVictim.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(6f, 0f) });
            farVictim.Push(new PlayerSnapshot { Time = 0.1f, Position = new GameVec2(6.1f, 0f) });

            var farHits = new List<Violation>();
            analyzer.AnalyzeKill(far, farVictim, 0.1f, 1.0f, 20f, farHits);
            True(farHits.Exists(v => v.Kind == ViolationKind.KillTooFar),
                "真远距离击杀必须抓住，否则误报修好了却漏报");
        }

        private static void TestTaskPositionTrust()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "ace-task-trust-" + Guid.NewGuid().ToString("N") + ".cfg");
            var cfg = new ApexCheatEnder.Config.AntiCheatConfig(new BepInEx.Configuration.ConfigFile(path, false));
            var analyzer = new BehaviorAnalyzer(cfg, null);
            var track = new PlayerTrack(1, "测试玩家", 1f);

            var task = new GameVec2(100f, 100f);   // 远超容差
            track.Push(new PlayerSnapshot { Time = 1f, Position = new GameVec2(0f, 0f) });

            // --- 位置不可信：不得产出远程任务证据 ---
            var untrusted = new List<Violation>();
            analyzer.AnalyzeTask(track, task, 1f, 4f, untrusted, absolutePositionTrusted: false);
            True(!untrusted.Exists(v => v.Kind == ViolationKind.RemoteTask),
                "任务点坐标不可信时不得产出远程任务误报");

            // --- 位置可信：同样的距离必须产出证据 ---
            var trusted = new List<Violation>();
            analyzer.AnalyzeTask(track, task, 1f, 4f, trusted, absolutePositionTrusted: true);
            True(trusted.Exists(v => v.Kind == ViolationKind.RemoteTask),
                "任务点坐标可信时必须保留远程任务判定");
        }

        /// <summary>
        /// 静态扫描不得把自己判成作弊。
        ///
        /// 好友现场日志：ACE 在两个回合里被自己的扫描器判成 AUM。
        /// 根因是作弊签名表（类型名 / 字符串标记）编译在本 DLL 内，
        /// 深度匹配会在自己的文件里搜到这些标记。
        /// </summary>
        private static void TestScannerSelfExclusion()
        {
            var source = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src"));
            var scanner = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "Core/ModScanner.cs"));

            True(scanner.Contains("IsSelf("), "静态扫描必须显式排除本插件自身");
            True(scanner.Contains("AntiCheatPlugin.PluginGuid"), "自身判定必须比 GUID");
            True(scanner.Contains("Assembly.Location"), "自身判定必须有程序集路径兜底");

            // 自身排除必须发生在黑名单匹配之前，否则深度匹配已经先把自己判了。
            var selfAt = scanner.IndexOf("if (IsSelf(plugin))", StringComparison.Ordinal);
            var blacklistAt = scanner.IndexOf("MatchBlacklist(plugin", StringComparison.Ordinal);
            True(selfAt >= 0 && blacklistAt > selfAt, "自身排除必须先于黑名单匹配");

            // 命中来源要如实记录，不能再把文件深度匹配写成「元数据匹配」。
            True(scanner.Contains("out var source") && scanner.Contains("DLL 类型标记匹配"),
                "黑名单命中来源必须区分元数据与 DLL 深度匹配");
        }

        /// <summary>
        /// Amethyst 互认的三条硬约束。
        ///
        /// Amethyst 用 PlayerPhysics.HandleRpc 的 callId 50 + 固定标识串做模组探测。
        /// ACE 只是旁听者，一旦越界就会破坏对方功能或冒名：
        ///   1. 不能发送任何探测包（发了对方会把 ACE 当成 Amethyst 用户 = 冒名）
        ///   2. 不能丢包（Amethyst 靠 return false 吃掉自己的包，ACE 再拦会破坏它）
        ///   3. 必须复原 reader.Position（否则 Amethyst 与游戏会解析错位）
        /// </summary>
        private static void TestAmethystPresenceSource()
        {
            var source = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src"));
            var core = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "Core/AmethystPresence.cs"));
            var patch = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "Patches/AmethystPresencePatch.cs"));

            // 协议常量必须与 Amethyst 实际实现一致
            True(core.Contains("\"AMETHYST_MOD_CLIENT_V1\""), "标识串必须与 Amethyst 一致");
            True(core.Contains("AmethystCallId = 50"), "callId 必须是 Amethyst 实际使用的 50");

            // 1. 只读不写
            True(!patch.Contains("StartRpcImmediately") && !patch.Contains("FinishRpcImmediately"),
                "旁听层不得发送任何 RPC");
            True(!core.Contains("StartRpcImmediately"), "核心层不得发送任何 RPC");

            // 2. 永不丢包
            var prefixStart = patch.IndexOf("private static bool Prefix", StringComparison.Ordinal);
            // Prefix 现在是类里的最后一个方法，用文件末尾定位。
            // （原来锚在 ExtractCallId 上，那个方法已随强类型参数改造一起删掉。）
            var prefixEnd = patch.LastIndexOf("}", StringComparison.Ordinal);
            True(prefixStart >= 0 && prefixEnd > prefixStart, "应能定位 Prefix 方法体");
            var prefix = patch.Substring(prefixStart, prefixEnd - prefixStart);
            True(prefix.Contains("return true;"), "Prefix 必须放行");
            True(!prefix.Contains("return false;"), "Prefix 不得丢包，否则会破坏 Amethyst 自身互认");

            // 3. 读取位置必须复原 + 必须抢在 Amethyst 之前
            True(core.Contains("reader.Position = start"), "旁听必须复原 MessageReader.Position");
            True(patch.Contains("HarmonyPriority(Priority.High)"),
                "必须抢在 Amethyst 的 Prefix 之前，否则它丢包后我们就看不到这些包");

            // 4. 防冒名：包里声明的 id 必须等于发送者自己的 id
            True(core.Contains("claimedId != sender.PlayerId"), "必须校验包内 id 与发送者一致");
        }

        /// <summary>
        /// 会议开始时的传送不得被判成「会议期间走动」。
        ///
        /// 好友现场日志：会议一开始，10 个玩家全部命中 MoveDuringMeeting
        /// （位移 3.15 ~ 21.43 单位）。那是游戏把所有人传送到会议桌，不是作弊。
        ///
        /// 两道闸必须都生效，同时「豁免过期后真的有人动」还得能抓到 ——
        /// 否则就是把检测整个废掉了。
        /// </summary>
        private static void TestMeetingEnterGrace()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "ace-meeting-" + Guid.NewGuid().ToString("N") + ".cfg");
            var cfg = new ApexCheatEnder.Config.AntiCheatConfig(new BepInEx.Configuration.ConfigFile(path, false));
            var analyzer = new BehaviorAnalyzer(cfg, null);

            // 闸一：上一帧不在会议里 → 这一帧就是入会那一跳
            var enter = new PlayerTrack(1, "P1", 0f);
            enter.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f), InMeeting = false });
            enter.Push(new PlayerSnapshot { Time = 0.1f, Position = new GameVec2(20f, 0f), InMeeting = true });
            var o1 = new List<Violation>();
            analyzer.AnalyzeMeetingMovement(enter, 0.1f, o1);
            True(!o1.Exists(v => v.Kind == ViolationKind.MoveDuringMeeting),
                "入会传送不得判为会议期间走动");

            // 闸二：仍在合法传送豁免窗口内
            var grace = new PlayerTrack(2, "P2", 0f);
            grace.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f), InMeeting = true });
            grace.Push(new PlayerSnapshot { Time = 0.1f, Position = new GameVec2(20f, 0f), InMeeting = true });
            grace.LastLegalTeleportTime = 0.05f;
            var o2 = new List<Violation>();
            analyzer.AnalyzeMeetingMovement(grace, 0.5f, o2);
            True(!o2.Exists(v => v.Kind == ViolationKind.MoveDuringMeeting),
                "会议开始豁免窗口内不得判定");

            // 豁免过期后，会议期间**持续**移动必须仍然能抓到
            var later = new PlayerTrack(3, "P3", 0f);
            later.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f), InMeeting = true });
            later.Push(new PlayerSnapshot { Time = 5f, Position = new GameVec2(20f, 0f), InMeeting = true });
            later.LastLegalTeleportTime = 0f;
            var o3 = new List<Violation>();
            // 第一次只记一次「疑似」，不报 —— 单次尖峰区分不了传送和移动
            analyzer.AnalyzeMeetingMovement(later, 5f, o3);
            True(!o3.Exists(v => v.Kind == ViolationKind.MoveDuringMeeting),
                "单次会议位移不得立即判定，必须连续才算");

            // 第二次仍在动 → 这才报
            later.Push(new PlayerSnapshot { Time = 5.1f, Position = new GameVec2(40f, 0f), InMeeting = true });
            analyzer.AnalyzeMeetingMovement(later, 5.1f, o3);
            True(o3.Exists(v => v.Kind == ViolationKind.MoveDuringMeeting),
                "连续移动必须判定");

            // 传送是「一跳就停」：动一次之后停住，永远攒不满连续次数
            var teleport = new PlayerTrack(4, "P4", 0f);
            teleport.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f), InMeeting = true });
            teleport.Push(new PlayerSnapshot { Time = 5f, Position = new GameVec2(20f, 0f), InMeeting = true });
            teleport.LastLegalTeleportTime = 0f;
            var o4 = new List<Violation>();
            analyzer.AnalyzeMeetingMovement(teleport, 5f, o4);
            teleport.Push(new PlayerSnapshot { Time = 5.1f, Position = new GameVec2(20f, 0f), InMeeting = true });
            analyzer.AnalyzeMeetingMovement(teleport, 5.1f, o4);
            True(!o4.Exists(v => v.Kind == ViolationKind.MoveDuringMeeting),
                "一次性传送后停住，不得判定为会议期间走动");
        }

        /// <summary>
        /// 手动踢人不得在构建列表时把 clientId 捕获进闭包。
        ///
        /// 踢人是不可逆操作。列表打开后玩家进出会重新分配 id / clientId，
        /// 用构建时的旧值会踢到别人头上。必须在点击时重新解析并校验身份。
        /// </summary>
        private static void TestManualKickSafety()
        {
            var source = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src"));
            var text = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "UI/SettingsWindow.cs"));

            var kickAt = text.IndexOf("\"踢出 \" + name", StringComparison.Ordinal);
            True(kickAt > 0, "应能找到手动踢人入口");

            // 取该入口后面的一段（到下一个 return list 为止）作为被检查区间
            var end = text.IndexOf("return list;", kickAt, StringComparison.Ordinal);
            True(end > kickAt, "应能界定踢人代码区间");
            var block = text.Substring(kickAt, end - kickAt);

            True(block.Contains("GameBridge.GetPlayers()"),
                "踢人时必须重新枚举玩家，确认目标仍然存在");
            True(block.Contains("GetPlayerId(current) != pid"),
                "必须校验玩家号仍然对得上");
            True(block.Contains("GetPlayerName(current) != name"),
                "必须校验昵称仍然对得上，防止 id 被复用后踢错人");
            True(block.Contains("GameBridge.GetClientIdByPlayerId(pid)"),
                "clientId 必须在点击时解析，不能提前捕获");
        }

        /// <summary>
        /// 设置窗口必须可调整大小，且尺寸要能持久化。
        ///
        /// 关键约束：拖拽重建布局必须节流 —— 重建会销毁并重建整窗对象，
        /// 每帧一次等于每秒造几千个对象。
        /// </summary>
        private static void TestResizableSettingsWindow()
        {
            var source = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src"));
            var ui = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "UI/SettingsWindow.cs"));
            var cfg = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "Config/AntiCheatConfig.cs"));

            True(ui.Contains("HandleResize"), "必须有右下角拖拽缩放处理");
            True(ui.Contains("ResizeGrip"), "必须有可见的缩放手柄");
            True(ui.Contains("_nextLiveRelayout"), "拖拽重建布局必须节流");
            True(ui.Contains("Mathf.Clamp(_resizeStartWidth") && ui.Contains("Mathf.Clamp(_resizeStartHeight"),
                "缩放必须限幅，否则能把窗口拖成 0 像素");
            True(ui.Contains("delta.x / scale"), "鼠标位移必须换算回未缩放前的窗口单位，否则手感会飘");

            // 尺寸必须写回配置，否则每次启动都回到默认值
            True(cfg.Contains("SettingsWindowWidth") && cfg.Contains("SettingsWindowHeight"),
                "窗口尺寸必须持久化到配置");
            True(ui.Contains("PersistWindowSize"), "松手后必须保存尺寸");

            // 重建时必须清掉旧引用，否则会残留指向已销毁对象的引用
            True(ui.Contains("_resizeGrip = null;") && ui.Contains("_built = false;"),
                "重建时必须重置引用与构建标志");
        }

        /// <summary>
        /// 通知频率：不能「只爆一次」，也不能刷屏。
        ///
        /// 线上问题：早期实现是「等级没升就不弹」，玩家一旦升到高风险，
        /// 之后所有作弊都还是高风险、等级不再变化 → 通知再也不弹，
        /// 表现就是「通知只爆一次，后面的作弊就不管了」。
        /// </summary>
        private static void TestNotificationFrequency()
        {
            const float Interval = 6f;

            // 正常状态不弹
            True(!PlayerVerdict.ShouldNotify(RiskLevel.Normal, RiskLevel.Normal, 3, 0, 100f, 0f, Interval),
                "正常等级不得弹通知");

            // 升级必弹（首次发现）
            True(PlayerVerdict.ShouldNotify(RiskLevel.HighRisk, RiskLevel.Normal, 1, 0, 10f, -1f, Interval),
                "等级升级必须弹");

            // 等级不变、无新证据 → 不弹（否则会刷屏）
            True(!PlayerVerdict.ShouldNotify(RiskLevel.HighRisk, RiskLevel.HighRisk, 5, 5, 100f, 0f, Interval),
                "等级不变且无新证据不得重复弹");

            // 等级不变、有新证据、冷却已过 → 必须弹（这是修掉「只爆一次」的关键）
            True(PlayerVerdict.ShouldNotify(RiskLevel.HighRisk, RiskLevel.HighRisk, 6, 5, 100f, 0f, Interval),
                "有新证据且冷却已过必须再弹，否则后续作弊全被静默");

            // 等级不变、有新证据、但还在冷却内 → 不弹（防刷屏）
            True(!PlayerVerdict.ShouldNotify(RiskLevel.HighRisk, RiskLevel.HighRisk, 6, 5, 3f, 0f, Interval),
                "冷却期内不得重复弹");
        }

        /// <summary>
        /// 破坏系统检测必须先确认「栈顶那条 RPC 就是 UpdateSystem」才归因。
        ///
        /// 现场问题：一次击杀同时报了 KillWhileNotImpostor 和 SabotageWhileNotImpostor，
        /// 用户看到的是「明明是杀人却报了破坏」。
        /// 根因是 RpcContext 记的是「当前正在处理的 RPC」而不是「谁调用了 UpdateSystem」——
        /// 处理击杀 RPC 途中游戏内部调用 UpdateSystem，就会被算到杀人者头上。
        /// </summary>
        private static void TestSabotageAttribution()
        {
            var source = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src"));
            var text = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "Patches/GameplayPatches.cs"));

            var start = text.IndexOf("class UpdateSystemPatch", StringComparison.Ordinal);
            True(start > 0, "应能找到破坏系统检测补丁");
            var end = text.IndexOf("SystemTypeCount", start, StringComparison.Ordinal);
            True(end > start, "应能界定补丁区间");
            var block = text.Substring(start, end - start);

            True(block.Contains("RpcContext.CallId != (int)RpcCalls.UpdateSystem"),
                "破坏归因前必须校验栈顶 RPC 就是 UpdateSystem");

            // 校验必须发生在取发送者之前，否则已经先归因了
            var checkAt = block.IndexOf("RpcContext.CallId", StringComparison.Ordinal);
            var senderAt = block.IndexOf("RpcContext.SenderTrack()", StringComparison.Ordinal);
            True(checkAt >= 0 && senderAt > checkAt, "校验必须早于取发送者");
        }

        /// <summary>
        /// 分辨率自愈的判定。
        ///
        /// 现场坏值：144×1（客户区 1 像素高，窗口 160×40）。它是**自锁死循环** ——
        /// Unity 把当前窗口尺寸当分辨率存下来，下次启动照此还原，于是永远好不了。
        /// 这里锁死「什么算不对劲」和「修复目标本身必须合理」。
        /// </summary>
        private static void TestResolutionPolicy()
        {
            True(ResolutionPolicy.NeedsRepair(144, 1), "实测的 144x1 必须判定为异常");
            True(ResolutionPolicy.NeedsRepair(160, 40), "160x40 必须判定为异常");
            True(ResolutionPolicy.NeedsRepair(1920, 100), "高度过小必须判定为异常");
            True(ResolutionPolicy.NeedsRepair(320, 1080), "宽度过小必须判定为异常");
            True(!ResolutionPolicy.NeedsRepair(1280, 600), "1280x600 必须判定为正常");
            True(!ResolutionPolicy.NeedsRepair(800, 480), "阈值边界必须判定为正常");
            True(!ResolutionPolicy.NeedsRepair(1920, 1080), "常规分辨率必须判定为正常");

            // 修复目标本身不合理时宁可不改，否则会越修越小
            True(ResolutionPolicy.IsValidTarget(1280, 600), "1280x600 是合法目标");
            True(!ResolutionPolicy.IsValidTarget(144, 1), "不得把目标设成坏值");
            True(!ResolutionPolicy.IsValidTarget(1280, 100), "目标高度过小应拒绝");
        }

        /// <summary>
        /// 反作弊图标：文件必须在、必须是嵌进 DLL 的、必须带透明通道。
        ///
        /// 这是整个插件里唯一一张外部画好的位图，容易在打包时漏掉 ——
        /// 漏了的话界面只是少个装饰，不会报错，所以必须有断言盯着。
        /// </summary>
        private static void TestAppIconResource()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var icon = System.IO.Path.Combine(root, "src/Resources/Icon.png");
            True(System.IO.File.Exists(icon), "图标文件必须存在");

            var csproj = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "ApexCheatEnder.csproj"));
            True(csproj.Contains("src\\Resources\\Icon.png"), "图标必须被 csproj 包含");
            True(csproj.Contains("ApexCheatEnder.Icon.png"), "图标必须有显式 LogicalName");

            var theme = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/UI/AceTheme.cs"));
            True(theme.Contains("ApexCheatEnder.Icon.png"), "加载路径必须与 LogicalName 一致");
            True(theme.Contains("if (_iconTried) return _icon"), "图标加载必须只尝试一次，失败不重试");

            // 两个使用点：标题栏与通知卡片
            var settings = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/UI/SettingsWindow.cs"));
            True(settings.Contains("AceTheme.Icon()"), "设置窗口标题栏必须使用图标");
            True(settings.Contains("titleLeft"), "有图标时标题必须右移，且图标缺失时不跑偏");

            var notify = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src/UI/NotificationPanel.cs"));
            True(notify.Contains("AceTheme.Icon()"), "通知卡片必须使用图标");
            True(notify.Contains("textLeft"), "有图标时通知文字必须右移");
        }

        private static void TestAutomaticMenuArt()
        {
            var source = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../src"));
            var art = System.IO.File.ReadAllText(System.IO.Path.Combine(source, "UI/MainMenuArt.cs"));
            foreach (var forbidden in new[] { "ShowMainMenuArt", "MainMenuArtUrl", "DownloadAsync", "Task.Run", "ChangeBackground" })
                True(!art.Contains(forbidden), "自动背景不得依赖旧配置、联网或手动切换：" + forbidden);
            True(art.Contains("static readonly int SelectedBackground"), "启动选择必须固定");

            // 现场日志证明：遍历场景根对象 + 泛型组件查询在好友那版 IL2CPP 上
            // 会抛 "Method unstripping failed"（133 次），且一次都没命中背景。
            // 这几条断言锁死「不许再走回那条路」。
            // 只检查真实代码：注释里会引用旧实现的 API 名字来解释为什么废弃它。
            var artCode = string.Join("\n", art
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//")));

            foreach (var forbidden in new[]
                     {
                         "GetRootGameObjects", "GetComponentsInChildren",
                         "FindBackgroundRenderer",
                     })
                True(!artCode.Contains(forbidden), "不得再遍历场景查找原背景：" + forbidden);

            // ══ 本文件是 Amethyst 的 AmethystMainMenuArt 的逐字移植 ══
            // 下面每条断言都对应一个「我自己改进过、结果出错」的地方，
            // 钉死它们，防止以后再自作聪明。
            True(art.Contains("new Vector3(0f, 0f, 600f)"), "位置必须写死 (0,0,600)，与 Amethyst 一致");
            True(art.Contains("PixelsPerUnit = 150f"), "像素密度必须与 Amethyst 一致（150）");
            True(art.Contains("(HideFlags)0x3D"), "贴图与精灵必须设 hideFlags |= 0x3D（含 DontUnloadUnusedAsset）");
            True(art.Contains("camera.orthographicSize * 2f"), "正交相机必须按 orthographicSize*2 算可视高度");
            True(art.Contains("viewHeight * camera.aspect"), "可视宽度必须由高度乘宽高比得到");
            True(art.Contains("GameObject.Find(AmbienceName)") && art.Contains("\"Ambience\""),
                "必须关掉原版氛围对象 Ambience");
            True(art.Contains("_ambience.SetActive(true)"), "离开菜单必须把 Ambience 恢复");

            // ══ 最关键的一步：关掉原版背景贴图 ══
            // Amethyst 的 AmethystMainMenuOptimize.Tick 里有 HideObject("BackgroundTexture")。
            // 我先前只关 Ambience，完全不知道 BackgroundTexture 才是游戏真正的背景贴图，
            // 它一直盖着自定义图 —— 所以图只在半透明面板里透出来。
            True(art.Contains("\"BackgroundTexture\""), "必须隐藏 BackgroundTexture，否则它永远盖着自定义图");
            True(art.Contains("HideVanillaLayers"), "必须有隐藏原版图层的步骤");
            True(art.Contains("RestoreVanillaLayers"), "离开菜单必须恢复原版图层");
            // 只隐藏 BackgroundTexture 一个就够了。
            // Amethyst 还会隐藏 WindowShine / MainUI/Tint / MaskedBlackScreen、
            // 关掉 LeftPanel / RightPanel 底板 —— 那是配合它自己重绘的界面。
            // 实测隐藏这些会导致主菜单「开始」按钮点不动（和菜单交互流程绑在一起）。
            // 用 artCode（已去注释）判断，否则注释里举的例子会被当成代码
            // 它现在出现在 NeverDisable 里（正确用法），关键是不能出现在隐藏名单里
            // 它就是现场那个黑框（7.7x4.9 的带窗口黑幕），必须关掉；
            // 之前误判成切换黑幕放进禁改名单，框才一直没去掉
            True(artCode.Contains("RightPanel/MaskedBlackScreen"), "MaskedBlackScreen 必须关掉（它就是那个黑框）");
            {
                // 它必须只出现在「要关」名单里，绝不能出现在禁改名单里
                var block = artCode.Substring(artCode.IndexOf("NeverDisable"));
                block = block.Substring(0, block.IndexOf("};"));
                True(!block.Contains("MaskedBlackScreen"),
                    "MaskedBlackScreen 不得留在禁改名单 —— 它就是那个黑框");
            }
            // GameObject.Find 传部分路径会返回 null —— 现场就是它导致黑框没关掉
            True(artCode.Contains("ResolveUnderMenu"), "必须逐级解析路径，不能用 GameObject.Find 传部分路径");
            // 游戏会重新启用这些渲染器，必须每帧重申
            True(artCode.Contains("ReassertHidden"), "必须每帧重申隐藏，否则抢不过游戏的重新启用");
            // 顶栏在 AccountManager 下，不在 MainMenuManager 树里 —— 扫菜单树永远找不到
            True(artCode.Contains("AccountTab/GameHeader/BarSprite"), "必须处理顶栏底板");
            True(artCode.Contains("FindObjectOfType<AccountManager>"), "顶栏要从 AccountManager 找");
            True(artCode.Contains("ClearedRenderers"), "透明色也要每帧重申");
            True(artCode.Contains("AspectScaler/RightPanel"), "必须关闭 RightPanel 底板 —— 它的外框会盖在背景上");
            // 递归关闭闯过大祸：一次关掉 132 个渲染器，把菜单切换黑幕也关了，
            // 导致主菜单与子菜单同时显示。必须精确点名，绝不递归。
            True(!art.Contains("DisableRenderersRecursive"), "不得递归关闭子树（会误伤菜单切换黑幕）");
            True(art.Contains("NeverDisable"), "必须有禁改名单，挡住 MaskedBlackScreen 等流程必需对象");
            True(art.Contains("LogLargeRenderers"), "必须有诊断：列不出对象时能直接定位是谁在画");
            // LeftPanel 现在**故意**隐藏（用户要求左侧面板底透明）。
            // 它是单一渲染器，安全；出问题的是「递归关整个子树」，见下面的禁改名单。
            True(artCode.Contains("AspectScaler/LeftPanel"), "LeftPanel 应在透明名单里");
            True(!artCode.Contains("\"WindowShine\""), "不得隐藏 WindowShine —— 非必需，少动少错");
            True(artCode.Contains("\"BackgroundTexture\""), "但必须隐藏 BackgroundTexture —— 否则背景永远露不出来");

            // 三条「自作聪明」的弯路，永久禁止
            // 只禁止「设置」自己的排序值；诊断里读取 sr.sortingOrder 是允许的
            True(!artCode.Contains("sortingOrder =") && !artCode.Contains("sortingOrder="),
                "不得设 sortingOrder —— 抬高会盖住游戏 UI（实测）");
            True(!artCode.Contains("TryComputeCoverSize") && !artCode.Contains("CoverSafetyFactor"),
                "不得自创「取所有相机最大值 × 安全系数」的尺寸算法，Amethyst 只用 Camera.main");
            True(!artCode.Contains("ScreenSpaceOverlay"), "不得用 Overlay 画布 —— 它会盖住游戏 UI");
            True(!artCode.Contains("Camera.allCameras"), "不得遍历相机挑一个，Amethyst 只用 Camera.main");

            // 反复重试刷 133 条同样的错误，本身就是缺陷；必须有熔断。
            // 反复重试刷 133 条同样的错误，本身就是缺陷；必须有熔断。
            True(art.Contains("MaxFailures") && art.Contains("_gaveUp"), "背景失败必须有熔断，不能无限重试");
            True(art.Contains("ex.StackTrace"), "背景失败必须记录调用栈，只有 Message 定位不到原因");

            // 排序只出现在画布上（取负值排到游戏 UI 之后）这类写法已经废弃，见上面三条弯路。

            // 反复重试刷 133 条同样的错误，本身就是缺陷；必须有熔断。
            True(art.Contains("MaxFailures") && art.Contains("_gaveUp"), "背景失败必须有熔断，不能无限重试");
            True(art.Contains("ex.StackTrace"), "背景失败必须记录调用栈，只有 Message 定位不到原因");

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
