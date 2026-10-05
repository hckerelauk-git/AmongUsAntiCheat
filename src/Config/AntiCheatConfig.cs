using BepInEx.Configuration;
using ApexCheatEnder.Core;

namespace ApexCheatEnder.Config
{
    /// <summary>
    /// 处置方式取值。
    /// 用中文字面量存是为了让配置文件可读、用户可手改。
    /// </summary>
    public static class DispositionModes
    {
        public const string None = "忽略";
        public const string Warn = "警告";
        public const string Kick = "踢出";
        public const string Ban = "封禁";

        public static readonly string[] All = { None, Warn, Kick, Ban };

        /// <summary>循环切换到下一档（用于设置界面里点击切换）。</summary>
        public static string Next(string current)
        {
            var i = System.Array.IndexOf(All, current);
            if (i < 0) i = 0;
            return All[(i + 1) % All.Length];
        }

        /// <summary>该档位是否具备踢人权限（踢出 / 封禁）。</summary>
        public static bool CanKick(string mode) => mode == Kick || mode == Ban;

        /// <summary>该档位是否要封禁。</summary>
        public static bool ShouldBan(string mode) => mode == Ban;
    }

    /// <summary>
    /// 反作弊的全部可调参数。
    ///
    /// 写进配置文件的每一条都必须满足两个标准：
    ///   1. 名字用日常说法，不用内部术语
    ///      （写「超速几次才记下来」，而不是「超速累计次数」）
    ///   2. 说明写「改了会怎样」，而不是把名字换个说法再说一遍
    ///
    /// 原因很直接：配置文件是普通玩家唯一的配置入口，
    /// 写得让人看不懂就等于这个功能不存在。
    ///
    /// 生成于 BepInEx/config/apex.cheat.ender.cfg
    /// </summary>
    public sealed class AntiCheatConfig
    {
        /// <summary>
        /// 换行符。配置文件里的说明经常要分好几行写成一段话。
        /// 用 (char)10 拼出来而不是写字面量，纯粹是历史原因，留着不影响。
        /// </summary>
        private static readonly string NL = ((char)10).ToString();

        /// <summary>
        /// 底层 ConfigFile。
        /// 设了 SaveOnConfigSet，任何一处改值都会自动落盘，调用方不用记得 Save。
        /// </summary>
        public ConfigFile File { get; }

        // ================= 开关 =================

        /// <summary>扫描别人装了什么作弊插件。</summary>
        public readonly ConfigEntry<bool> EnableStaticScan;
        /// <summary>盯着所有人的走位，抓瞬移和超速。</summary>
        public readonly ConfigEntry<bool> EnableBehaviorScan;
        /// <summary>检查击杀、爬管道等动作是否合法。</summary>
        public readonly ConfigEntry<bool> EnableEventScan;
        /// <summary>检查有没有人穿墙跑。</summary>
        public readonly ConfigEntry<bool> EnableWallClipCheck;
        /// <summary>把每一次检测的原始数据都打进日志。</summary>
        public readonly ConfigEntry<bool> VerboseLogging;

        // ================= 采样 =================

        public readonly ConfigEntry<float> SampleInterval;
        public readonly ConfigEntry<float> RoundStartGracePeriod;
        public readonly ConfigEntry<float> PositionJitterTolerance;

        // ================= 走位判定 =================

        public readonly ConfigEntry<float> MaxSpeedTolerance;
        public readonly ConfigEntry<float> TeleportMinDistance;
        public readonly ConfigEntry<int> SpeedStrikeCount;

        // ================= 动作判定 =================

        public readonly ConfigEntry<float> KillDistanceTolerance;
        public readonly ConfigEntry<float> KillCooldownTolerance;
        public readonly ConfigEntry<float> TaskSpeedTolerance;
        public readonly ConfigEntry<float> RemoteTaskTolerance;
        public readonly ConfigEntry<float> MeetingMoveTolerance;

        // ================= 处置 =================

        public readonly ConfigEntry<bool> AllowAutoKick;
        public readonly ConfigEntry<string> DispositionMode;

        // ================= 界面 =================

        public readonly ConfigEntry<bool> ShowDesktopSplash;
        public readonly ConfigEntry<bool> ShowNotifications;
        public readonly ConfigEntry<float> NotificationDuration;

        public readonly ConfigEntry<bool> ShowChatAbuseNotice;
        public readonly ConfigEntry<string> ChatAbuseKeywords;
        public readonly ConfigEntry<bool> ShowRepeatedChatNotice;
        public readonly ConfigEntry<int> RepeatedChatThreshold;
        public readonly ConfigEntry<string> RuleGroupMapping;

        /// <summary>识别并标记同样装了本插件的玩家。</summary>
        public readonly ConfigEntry<bool> AcePresenceEnabled;

        /// <summary>标记文案（显示在对方名字后面）。</summary>
        public readonly ConfigEntry<string> AcePresenceTag;

        /// <summary>是否识别并标记装了 Amethyst 的玩家（只旁听，不发送）。</summary>
        public readonly ConfigEntry<bool> AmethystPresenceEnabled;

        /// <summary>Amethyst 用户的名字标记文案。</summary>
        public readonly ConfigEntry<string> AmethystPresenceTag;

        /// <summary>是否给没装模组的玩家也加标记。</summary>
        public readonly ConfigEntry<bool> MarkVanillaPlayers;

        /// <summary>没装模组的玩家的标记文案。</summary>
        public readonly ConfigEntry<string> VanillaPlayerTag;

        /// <summary>左上角是否显示帧率 / 延迟 / 房主状态条。</summary>
        public readonly ConfigEntry<bool> ShowStatsHud;

        /// <summary>设置窗口宽度（像素，缩放前）。拖右下角改完会自动写回这里。</summary>
        public readonly ConfigEntry<int> SettingsWindowWidth;

        /// <summary>设置窗口高度（像素，缩放前）。</summary>
        public readonly ConfigEntry<int> SettingsWindowHeight;

        /// <summary>检测到游戏窗口尺寸异常时是否自动改回可用分辨率。</summary>
        public readonly ConfigEntry<bool> AutoFixResolution;

        /// <summary>自动修复用的宽度。</summary>
        public readonly ConfigEntry<int> FixResolutionWidth;

        /// <summary>自动修复用的高度。</summary>
        public readonly ConfigEntry<int> FixResolutionHeight;

        // ================= 爬管道 / 滑索 =================

        public readonly ConfigEntry<bool> VentNonImpostor;
        public readonly ConfigEntry<bool> VentRemote;
        public readonly ConfigEntry<bool> VentForgedId;
        public readonly ConfigEntry<bool> VentForceOther;
        public readonly ConfigEntry<bool> ZiplineAbuse;
        public readonly ConfigEntry<bool> VentDuringMeeting;
        public readonly ConfigEntry<float> VentDistanceTolerance;

        // ================= 记录 =================

        public readonly ConfigEntry<bool> RecordPlayerHistory;
        public readonly ConfigEntry<bool> RecordCheatHistory;

        // ================= 网络防护 =================

        public readonly ConfigEntry<bool> RpcFloodDetection;
        public readonly ConfigEntry<int> RpcRateLimit;
        public readonly ConfigEntry<bool> SnapRateDetection;
        public readonly ConfigEntry<int> SnapRateLimit;
        public readonly ConfigEntry<bool> BlockEarlyMeeting;
        public readonly ConfigEntry<float> EarlyMeetingGrace;
        public readonly ConfigEntry<bool> OversizedPacketCheck;

        // ================= 进阶检测 =================

        /// <summary>抓非法破坏（非内鬼破坏 / 会议中破坏 / 越界目标）。</summary>
        public readonly ConfigEntry<bool> SabotageCheck;

        /// <summary>抓角色动作异常（非变形者变形 / 非守护天使保护）。</summary>
        public readonly ConfigEntry<bool> RoleActionCheck;

        /// <summary>抓聊天刷屏与非法消息内容。</summary>
        public readonly ConfigEntry<bool> ChatCheck;

        /// <summary>聊天频率上限（条 / 10 秒）。</summary>
        public readonly ConfigEntry<int> ChatRateLimit;

        /// <summary>抓非法昵称（空、超长、含控制字符）。</summary>
        public readonly ConfigEntry<bool> NameCheck;

        /// <summary>昵称最大长度（字符）。</summary>
        public readonly ConfigEntry<int> NameMaxLength;

        // ================= 性能 =================

        /// <summary>不更新死亡玩家（死人不需要做运动学分析）。</summary>
        public readonly ConfigEntry<bool> PerfDontUpdateDead;

        /// <summary>死亡玩家的更新跳帧间隔（每 N 次采样才算一次）。</summary>
        public readonly ConfigEntry<int> PerfDeadSkipFrames;

        /// <summary>低负载模式：整体降低检测频率，牺牲灵敏度换流畅度。</summary>
        public readonly ConfigEntry<bool> PerfLowLoad;

        /// <summary>性能探针：测量本模组自身的每帧开销，超阈值时打日志。</summary>
        public readonly ConfigEntry<bool> PerfProbe;

        // ================= 白名单 =================

        public readonly ConfigEntry<string> TrustedPluginGuids;
        public readonly ConfigEntry<string> TrustedPluginNames;

        // ================= 封禁名单 =================

        /// <summary>启用内置封禁名单。</summary>
        public readonly ConfigEntry<bool> EnableBanList;

        /// <summary>自己追加的封禁名单条目（配置字符串）。</summary>
        public readonly ConfigEntry<string> BanListExtra;

        /// <summary>是否启用在线共享封禁名单。</summary>
        public readonly ConfigEntry<bool> EnableRemoteBanList;

        /// <summary>在线名单的 HTTP 端点。</summary>
        public readonly ConfigEntry<string> BanListEndpoint;

        /// <summary>
        /// 在线名单的写入令牌。
        ///
        /// **留空 = 只写本地，不推送。** 这样共享名单不会被任意用户的客户端改动。
        /// 只有填了令牌的机器（也就是名单维护者）才会把封禁推上去。
        /// </summary>
        public readonly ConfigEntry<string> BanListWriteToken;

        /// <summary>在线名单刷新间隔（小时）。</summary>
        public readonly ConfigEntry<float> BanListRefreshHours;

        /// <summary>
        /// 旧版配置键迁移表：(旧分区, 旧键) → (新分区, 新键)。
        ///
        /// 1.1.5 把配置项文案由口语改为术语，键名随之变化。
        /// BepInEx 是**按键名**存值的 —— 不做迁移的话，老用户升级后
        /// 全部设置会静默回到默认值，而他们完全不知道发生了什么。
        /// 这种「静默重置」是最难排查的一类问题：功能没坏，只是「自己变回去了」。
        /// </summary>
        private static readonly (string OldSection, string OldKey, string NewSection, string NewKey)[] LegacyKeys =
        {
            ("开哪些检测", "扫描作弊插件", "检测模块", "静态插件扫描"),
            ("开哪些检测", "检测瞬移和超速", "检测模块", "运动学检测"),
            ("开哪些检测", "检查动作是否合法", "检测模块", "动作合法性检测"),
            ("开哪些检测", "检测穿墙", "检测模块", "穿墙检测"),
            ("开哪些检测", "输出详细日志", "检测模块", "详细日志"),
            ("多久看一次位置", "看位置的间隔（秒）", "采样", "采样间隔（秒）"),
            ("多久看一次位置", "开局后先不管几秒", "采样", "开局宽限期（秒）"),
            ("多久看一次位置", "多小的位移算正常抖动", "采样", "位移抖动容差"),
            ("怎么算跑太快", "允许比正常快几倍", "速度与位移阈值", "速度上限倍率"),
            ("怎么算跑太快", "一下挪多远算瞬移", "速度与位移阈值", "瞬移判定距离"),
            ("怎么算跑太快", "超速几次才记下来", "速度与位移阈值", "超速确认次数"),
            ("怎么算动作违规", "隔多远能砍人（额外放宽）", "动作合法性阈值", "击杀距离容差"),
            ("怎么算动作违规", "冷却能提前多久（秒）", "动作合法性阈值", "击杀冷却容差（秒）"),
            ("怎么算动作违规", "做任务允许快几倍", "动作合法性阈值", "任务速度倍率"),
            ("怎么算动作违规", "隔多远能交任务（额外放宽）", "动作合法性阈值", "任务提交距离容差"),
            ("怎么算动作违规", "会议时允许走多远", "动作合法性阈值", "会议位移容差"),
            ("抓到之后怎么办", "动手的方式", "处置策略", "处置方式"),
            ("抓到之后怎么办", "自动踢人（不用手动点）", "处置策略", "自动执行（免确认）"),
            ("界面显示", "显示开机启动动画", "界面与提示", "启动动画"),
            ("界面显示", "屏幕顶部弹出提醒", "界面与提示", "屏幕通知"),
            ("界面显示", "提醒停留几秒", "界面与提示", "通知停留时长（秒）"),
            ("界面显示", "疑似骂人短提示", "界面与提示", "聊天内容本地提示"),
            ("界面显示", "疑似骂人关键词", "界面与提示", "触发关键词"),
            ("界面显示", "重复聊天本地提示", "界面与提示", "重复消息提示"),
            ("界面显示", "重复几次才提示", "界面与提示", "重复确认次数"),
            ("界面显示", "标记同装 ACE 的玩家", "界面与提示", "ACE 用户标记"),
            ("界面显示", "标记写成什么", "界面与提示", "ACE 标记文本"),
            ("界面显示", "标记装了 Amethyst 的玩家", "界面与提示", "Amethyst 用户标记"),
            ("界面显示", "标记非模组玩家", "界面与提示", "原版用户标记"),
            ("界面显示", "非模组玩家标记写成什么", "界面与提示", "原版标记文本"),
            // 中间态：本会话先把键名改成「原生*」，随后又统一为「原版*」。
            // 已经跑过中间版本的用户，配置里存的就是「原生*」。
            ("界面与提示", "原生用户标记", "界面与提示", "原版用户标记"),
            ("界面与提示", "原生标记文本", "界面与提示", "原版标记文本"),
            ("界面显示", "Amethyst 标记写成什么", "界面与提示", "Amethyst 标记文本"),
            ("界面显示", "显示帧率延迟房主", "界面与提示", "状态条"),
            ("界面显示", "设置窗口宽度", "界面与提示", "窗口宽度"),
            ("界面显示", "设置窗口高度", "界面与提示", "窗口高度"),
            ("界面显示", "窗口尺寸异常时自动改回", "界面与提示", "分辨率自愈"),
            ("界面显示", "自动改回多宽", "界面与提示", "修复宽度"),
            ("界面显示", "自动改回多高", "界面与提示", "修复高度"),
            ("爬管道和滑索", "抓普通人爬管道", "通风管与滑索", "非内鬼使用通风管"),
            ("爬管道和滑索", "抓隔着屏幕爬管道", "通风管与滑索", "远距离使用通风管"),
            ("爬管道和滑索", "抓伪造管道编号", "通风管与滑索", "伪造通风管编号"),
            ("爬管道和滑索", "抓强迫别人爬管道", "通风管与滑索", "强制他人离开通风管"),
            ("爬管道和滑索", "抓滑索滥用", "通风管与滑索", "滑索异常使用"),
            ("爬管道和滑索", "抓开会时爬管道", "通风管与滑索", "会议期间使用通风管"),
            ("爬管道和滑索", "爬管道允许离多远", "通风管与滑索", "通风管距离容差"),
            ("记录", "记录谁进过房间", "记录与留档", "记录房间成员"),
            ("记录", "记录作弊判定", "记录与留档", "记录判定结果"),
            ("网络防护", "抓数据包刷屏", "网络层防护", "RPC 洪水检测"),
            ("网络防护", "10 秒内最多几次数据包", "网络层防护", "RPC 频率上限（次/10 秒）"),
            ("网络防护", "抓瞬移式位置同步", "网络层防护", "异常位置同步检测"),
            ("网络防护", "10 秒内最多几次强制同步", "网络层防护", "位置同步上限（次/10 秒）"),
            ("网络防护", "抓开局乱开会", "网络层防护", "开局误报检测"),
            ("网络防护", "开局后几秒内不许开会", "网络层防护", "开局保护期（秒）"),
            ("网络防护", "抓超大数据包", "网络层防护", "超大包检测"),
            ("进阶检测", "抓非法破坏", "进阶检测", "非法破坏检测"),
            ("进阶检测", "抓角色动作异常", "进阶检测", "角色能力检测"),
            ("进阶检测", "抓聊天刷屏和非法消息", "进阶检测", "聊天频率与内容检测"),
            ("进阶检测", "10 秒内最多几条消息", "进阶检测", "聊天频率上限（条/10 秒）"),
            ("进阶检测", "抓非法昵称", "进阶检测", "昵称合法性检测"),
            ("进阶检测", "昵称最长多少字", "进阶检测", "昵称长度上限"),
            ("性能", "不更新死亡玩家", "性能", "跳过死亡玩家"),
            ("性能", "死亡玩家跳几帧才算一次", "性能", "死亡玩家采样降频（帧）"),
            ("性能", "性能探针（排查卡顿用）", "性能", "性能探针"),
            ("白名单", "信任的插件 GUID", "插件白名单", "受信任 GUID"),
            ("白名单", "信任的插件名", "插件白名单", "受信任插件名"),
            ("封禁名单", "启用封禁名单", "封禁名单", "启用本地名单"),
            ("封禁名单", "自己追加的名单", "封禁名单", "自定义条目")
        };

        /// <summary>
        /// 把旧键的值搬到新键上，然后删除旧键。
        ///
        /// 三种情况分别处理：
        ///   1. 旧键是默认值     → 用户没改过，直接删除，不留垃圾键
        ///   2. 新键已被显式改过 → 用户已经用新版调过了，尊重新值，只删旧键
        ///   3. 其余             → 把旧值搬过去
        /// </summary>
        private static void MigrateLegacyKeys(ConfigFile cfg)
        {
            var moved = 0;

            foreach (var m in LegacyKeys)
            {
                try
                {
                    var old = cfg[m.OldSection, m.OldKey];
                    if (old == null) continue;

                    var now = cfg[m.NewSection, m.NewKey];
                    if (now == null) { cfg.Remove(old.Definition); continue; }

                    var untouched = old.BoxedValue == null || old.BoxedValue.Equals(old.DefaultValue);
                    var alreadySet = now.BoxedValue != null && !now.BoxedValue.Equals(now.DefaultValue);

                    if (!untouched && !alreadySet)
                    {
                        now.BoxedValue = old.BoxedValue;
                        moved++;
                    }

                    cfg.Remove(old.Definition);
                }
                catch { }
            }

            if (moved > 0)
                ApexCheatEnder.Core.BanListRemote.LogInfo?.Invoke(
                    "[配置] 已从旧版键名迁移 " + moved + " 项设置。");
        }

        public AntiCheatConfig(ConfigFile cfg)
        {
            File = cfg;

            // 任何一处改值立刻写盘，调用方不用记得手动 Save。
            File.SaveOnConfigSet = true;

            // ---------------- 开哪些检测 ----------------
            const string A = "检测模块";
            EnableStaticScan = cfg.Bind(A, "静态插件扫描", true,
                "扫描已加载的 BepInEx 插件，匹配已知作弊特征。误报率极低，建议保持开启。");
            EnableBehaviorScan = cfg.Bind(A, "运动学检测", true,
                "按采样间隔记录玩家位置，检测瞬移与持续超速。");
            EnableEventScan = cfg.Bind(A, "动作合法性检测", true,
                "校验击杀、通风管等动作在当前角色状态下是否允许。");
            EnableWallClipCheck = cfg.Bind(A, "穿墙检测", false,
                "检测运动轨迹是否穿过墙体碰撞体。" + NL +
                "开销略高，网络抖动时易误报，默认关闭。");
            VerboseLogging = cfg.Bind(A, "详细日志", false,
                "输出每次检测的原始数据。" + NL +
                "仅在排查误报时开启，日志增长很快。");

            // ---------------- 多久看一次 ----------------
            const string B = "采样";
            SampleInterval = cfg.Bind(B, "采样间隔（秒）", 0.10f,
                new ConfigDescription(
                    "位置采样的时间间隔。越小越灵敏，开销也越高。",
                    new AcceptableValueRange<float>(0.02f, 1.0f)));
            RoundStartGracePeriod = cfg.Bind(B, "开局宽限期（秒）", 4.0f,
                new ConfigDescription(
                    "对局开始后暂不判定的时长，用于规避加载阶段的位置跳变。",
                    new AcceptableValueRange<float>(0f, 30f)));
            PositionJitterTolerance = cfg.Bind(B, "位移抖动容差", 0.35f,
                "低于此距离的位移视为网络抖动，不计入判定。");

            // ---------------- 多快算超速 ----------------
            const string C = "速度与位移阈值";
            MaxSpeedTolerance = cfg.Bind(C, "速度上限倍率", 1.6f,
                new ConfigDescription(
                    "允许的最大移动速度相对正常速度的倍率。",
                    new AcceptableValueRange<float>(1.0f, 5.0f)));
            TeleportMinDistance = cfg.Bind(C, "瞬移判定距离", 8.0f,
                new ConfigDescription(
                    "单次采样位移超过此距离即判定为瞬移。",
                    new AcceptableValueRange<float>(1f, 60f)));

            // 一次性迁移：旧默认 4.5 会误伤延迟校正，升到 8.0。
            // 只在用户没自己改过（值仍是旧默认）时才动。
            if (System.Math.Abs(TeleportMinDistance.Value - 4.5f) < 0.001f)
                TeleportMinDistance.Value = 8.0f;
            SpeedStrikeCount = cfg.Bind(C, "超速确认次数", 3,
                new ConfigDescription(
                    "连续超速达到此次数才记为一次命中，用于抑制网络抖动导致的误报。",
                    new AcceptableValueRange<int>(1, 20)));

            // ---------------- 多离谱算动作违规 ----------------
            const string D = "动作合法性阈值";
            KillDistanceTolerance = cfg.Bind(D, "击杀距离容差", 0.75f,
                new ConfigDescription(
                    "在允许击杀距离基础上额外放宽的余量。",
                    new AcceptableValueRange<float>(0f, 5f)));
            KillCooldownTolerance = cfg.Bind(D, "击杀冷却容差（秒）", 0.35f,
                new ConfigDescription(
                    "允许击杀冷却提前的秒数，用于补偿网络延迟。",
                    new AcceptableValueRange<float>(0f, 5f)));
            TaskSpeedTolerance = cfg.Bind(D, "任务速度倍率", 1.5f,
                new ConfigDescription(
                    "任务完成速度相对正常速度的允许倍率。",
                    new AcceptableValueRange<float>(1.0f, 5f)));
            RemoteTaskTolerance = cfg.Bind(D, "任务提交距离容差", 2.0f,
                new ConfigDescription(
                    "在允许距离基础上额外放宽的任务提交余量。",
                    new AcceptableValueRange<float>(0f, 10f)));
            MeetingMoveTolerance = cfg.Bind(D, "会议位移容差", 0.75f,
                new ConfigDescription(
                    "会议期间允许的位移余量，用于排除入会传送造成的误报。",
                    new AcceptableValueRange<float>(0f, 5f)));

            // ---------------- 抓到之后怎么办 ----------------
            const string E = "处置策略";
            DispositionMode = cfg.Bind(E, "处置方式", DispositionModes.Warn,
                new ConfigDescription(
                    "命中规则后执行的动作：" + NL +
                "  忽略 — 仅记录，不执行动作" + NL +
                "  警告 — 屏幕提示" + NL +
                "  踢出 — 移出房间（需为房主）" + NL +
                "  封禁 — 移出并加入本地黑名单（需为房主）" + NL +
                "判定模型为规则命中：命中即记录，命中确定性规则立即处置，不采用分数累计。",
                    new AcceptableValueList<string>(DispositionModes.All)));
            AllowAutoKick = cfg.Bind(E, "自动执行（免确认）", false,
                "命中确定性规则后自动执行处置，无需手动确认。" + NL +
                "建议先保持关闭，确认无误报后再开启。");

            // ---------------- 界面 ----------------
            const string F = "界面与提示";
            ShowDesktopSplash = cfg.Bind(F, "启动动画", true,
                "游戏启动时显示 ACE 加载动画。");
            ShowNotifications = cfg.Bind(F, "屏幕通知", true,
                "命中检测规则时在屏幕上方显示通知。");
            NotificationDuration = cfg.Bind(F, "通知停留时长（秒）", 12f,
                new ConfigDescription(
                    "通知自动消失的时间。" + NL +
                "默认 12 秒，可读性优于 5 秒；上限 60 秒。",
                    new AcceptableValueRange<float>(2f, 60f)));

            ShowChatAbuseNotice = cfg.Bind(F, "聊天内容本地提示", true,
                "聊天内容命中关键词时本地显示提示图片 1.2 秒，Esc 关闭，10 秒冷却。" + NL +
                "关键词无法理解语境，引用或讨论亦可能触发；仅标记为疑似，不计入作弊证据、不执行处置，可随时关闭。");
            ChatAbuseKeywords = cfg.Bind(F, "触发关键词", ChatAbuseNoticePolicy.DefaultKeywords,
                "自定义关键词，逗号分隔；留空则不提示。仅做直接子串匹配，不推断语境。" + NL +
                "建议避免宽泛单字以减少误报。不影响聊天发送与反作弊功能。");

            ShowRepeatedChatNotice = cfg.Bind(F, "重复消息提示", false,
                "同一玩家在 10 秒内重复发送相同消息时仅本地提示；不拦截聊天、不生成作弊证据。");
            RepeatedChatThreshold = cfg.Bind(F, "重复确认次数", 3,
                new ConfigDescription(
                    "10 秒内相同消息达到此次数后提示，随后 10 秒冷却。", new AcceptableValueRange<int>(3, 10)));
            RuleGroupMapping = cfg.Bind("本地规则分组", "规则到分组映射", RuleGroups.Defaults(),
                "格式：Teleport=移动;IllegalChat=消息。仅影响本地历史分类，不影响检测和处置。" + NL +
                "允许分组：移动、动作、会议、消息、网络、静态。非法映射回退内置分组，不执行代码、不联网。");

            // ---------------- 同装 ACE 的玩家 ----------------
            AcePresenceEnabled = cfg.Bind(F, "ACE 用户标记", true,
                "在同时安装 ACE 的玩家名字后附加标记。");
            AcePresenceTag = cfg.Bind(F, "ACE 标记文本", Core.PresenceTags.AceDefault,
                "ACE 用户标记的显示文本。");

            AmethystPresenceEnabled = cfg.Bind(F, "Amethyst 用户标记", true,
                "在安装 Amethyst 的玩家名字后附加标记。");
            MarkVanillaPlayers = cfg.Bind(F, "原版用户标记", true,
                "为未安装任何模组的玩家附加标记。");
            VanillaPlayerTag = cfg.Bind(F, "原版标记文本", Core.PresenceTags.VanillaDefault,
                "原版玩家标记的显示文本。");

            AmethystPresenceTag = cfg.Bind(F, "Amethyst 标记文本", Core.PresenceTags.AmethystDefault,
                "Amethyst 用户标记的显示文本。");

            // 一次性迁移：旧默认是紫心 💜，新默认是粉心 💗。
            // BepInEx 把默认值写进 cfg 文件后，改默认值对已有配置不再生效，
            // 所以这里显式把「恰好还等于旧默认值」的配置迁过去。
            // 只在用户没自己改过时才动，不会覆盖自定义文案。
            if (AmethystPresenceTag.Value == Core.PresenceTags.LegacyAmethystTag)
                AmethystPresenceTag.Value = Core.PresenceTags.AmethystDefault;

            // 同理：旧默认「原本玩家」是笔误，正确是「原版玩家」。
            if (VanillaPlayerTag.Value == Core.PresenceTags.LegacyVanillaTag)
                VanillaPlayerTag.Value = Core.PresenceTags.VanillaDefault;

            ShowStatsHud = cfg.Bind(F, "状态条", true,
                "屏幕底部显示帧率、延迟与房主身份。");

            SettingsWindowWidth = cfg.Bind(F, "窗口宽度", 1000,
                new ConfigDescription(
                    "设置窗口的宽度（像素）。",
                    new AcceptableValueRange<int>(640, 1920)));
            SettingsWindowHeight = cfg.Bind(F, "窗口高度", 700,
                new ConfigDescription(
                    "设置窗口的高度（像素）。",
                    new AcceptableValueRange<int>(460, 1200)));

            AutoFixResolution = cfg.Bind(F, "分辨率自愈", true,
                "检测到窗口尺寸异常时自动恢复。" + NL +
                "用于打断「窗口被压小 → Unity 记录小尺寸 → 下次启动仍为小窗口」的循环。");
            FixResolutionWidth = cfg.Bind(F, "修复宽度", 1280,
                new ConfigDescription(
                    "自动恢复时使用的宽度。",
                    new AcceptableValueRange<int>(800, 3840)));
            FixResolutionHeight = cfg.Bind(F, "修复高度", 600,
                new ConfigDescription(
                    "自动恢复时使用的高度。",
                    new AcceptableValueRange<int>(480, 2160)));


            // ---------------- 爬管道 / 滑索 ----------------
            const string G = "通风管与滑索";
            VentNonImpostor = cfg.Bind(G, "非内鬼使用通风管", true,
                "检测不具备通风管能力的角色使用通风管。");
            VentRemote = cfg.Bind(G, "远距离使用通风管", true,
                "检测与通风管距离超出允许范围的使用行为。");
            VentForgedId = cfg.Bind(G, "伪造通风管编号", true,
                "检测使用不存在的通风管编号。");
            VentForceOther = cfg.Bind(G, "强制他人离开通风管", true,
                "检测非房主强制将他人移出通风管。");
            ZiplineAbuse = cfg.Bind(G, "滑索异常使用", true,
                "检测滑索使用时机非法或坐标越界。");
            VentDuringMeeting = cfg.Bind(G, "会议期间使用通风管", true,
                "检测会议期间使用通风管。");
            VentDistanceTolerance = cfg.Bind(G, "通风管距离容差", 2.0f,
                new ConfigDescription(
                    "在允许距离基础上额外放宽的通风管余量。",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            // ---------------- 记录 ----------------
            const string H = "记录与留档";
            RecordPlayerHistory = cfg.Bind(H, "记录房间成员", true,
                "每次进入对局时记录房间成员，写入 PlayerHistory.txt。");
            RecordCheatHistory = cfg.Bind(H, "记录判定结果", true,
                "每次命中规则时记录玩家与规则，写入 CheatHistory.txt，可作为误判复核依据。");

            // ---------------- 网络防护 ----------------
            const string I = "网络层防护";
            RpcFloodDetection = cfg.Bind(I, "RPC 洪水检测", true,
                "检测单位时间内 RPC 数量异常。");
            RpcRateLimit = cfg.Bind(I, "RPC 频率上限（次/10 秒）", 60,
                new ConfigDescription(
                    "10 秒窗口内允许的最大 RPC 数量。",
                    new AcceptableValueRange<int>(5, 200)));
            SnapRateDetection = cfg.Bind(I, "异常位置同步检测", true,
                "检测强制位置同步的异常频率。");
            SnapRateLimit = cfg.Bind(I, "位置同步上限（次/10 秒）", 6,
                new ConfigDescription(
                    "10 秒窗口内允许的最大位置同步次数。",
                    new AcceptableValueRange<int>(1, 40)));
            BlockEarlyMeeting = cfg.Bind(I, "开局误报检测", true,
                "检测开局保护期内发起会议或报告尸体。");
            EarlyMeetingGrace = cfg.Bind(I, "开局保护期（秒）", 15f,
                new ConfigDescription(
                    "开局后禁止发起会议的时间。",
                    new AcceptableValueRange<float>(0f, 60f)));
            OversizedPacketCheck = cfg.Bind(I, "超大包检测", true,
                "检测异常大小的数据包，可能意图拖垮房间。");

            // ---------------- 进阶检测 ----------------
            const string L = "进阶检测";
            SabotageCheck = cfg.Bind(L, "非法破坏检测", true,
                "检测非内鬼阵营触发破坏系统。");
            RoleActionCheck = cfg.Bind(L, "角色能力检测", true,
                "检测不具备对应能力的角色执行变形或保护。");
            ChatCheck = cfg.Bind(L, "聊天频率与内容检测", true,
                "检测聊天刷屏与非法消息内容。");
            ChatRateLimit = cfg.Bind(L, "聊天频率上限（条/10 秒）", 8,
                new ConfigDescription(
                    "10 秒窗口内允许的最大聊天消息数。",
                    new AcceptableValueRange<int>(3, 50)));
            NameCheck = cfg.Bind(L, "昵称合法性检测", true,
                "检测空昵称、超长昵称与含控制字符的昵称。");
            NameMaxLength = cfg.Bind(L, "昵称长度上限", 20,
                new ConfigDescription(
                    "允许的最大昵称长度。",
                    new AcceptableValueRange<int>(5, 60)));

            // ---------------- 性能 ----------------
            const string M = "性能";
            PerfDontUpdateDead = cfg.Bind(M, "跳过死亡玩家", true,
                "死亡玩家不参与运动学分析。");
            PerfDeadSkipFrames = cfg.Bind(M, "死亡玩家采样降频（帧）", 5,
                new ConfigDescription(
                    "死亡玩家的采样降频间隔（每 N 次采样计算一次）。",
                    new AcceptableValueRange<int>(1, 30)));
            PerfLowLoad = cfg.Bind(M, "低负载模式", false,
                "降低整体检测频率，以灵敏度换取流畅度。");
            PerfProbe = cfg.Bind(M, "性能探针", false,
                "测量本模组自身的每帧开销，超过阈值时输出日志。");

            // ---------------- 白名单 ----------------
            const string J = "插件白名单";            TrustedPluginGuids = cfg.Bind(J, "受信任 GUID", "",
                "以英文逗号分隔。列入的插件即使命中特征也不会拦截。一般无需填写。");
            TrustedPluginNames = cfg.Bind(J, "受信任插件名", "",
                "以英文逗号分隔。按插件名匹配，用于无法识别 GUID 的情况。一般无需填写。");

            // ---------------- 封禁名单 ----------------
            const string K = "封禁名单";
            EnableBanList = cfg.Bind(K, "启用本地名单", true,
                "内置一份名单，命中的人会被记为确定级证据。" + NL +
                "匹配依据是好友码 / 平台 ID，**不是名字** —— 改名甩不掉。" + NL +
                "命中之后怎么处置，由上面「动手的方式」决定（默认只警告）。");
            BanListExtra = cfg.Bind(K, "自定义条目", "",
                "格式：名字|好友码|平台ID|备注，多条以分号分隔。" + NL +
                "示例：张三|zhangsan#1234||炸房" + NL +
                "好友码与平台 ID 至少填写其一。仅填名字的条目会被忽略 ——" +
                "名字可任意更改，仅凭名字匹配必然误伤同名玩家。" + NL +
                "修改后立即生效，无需重启。");

            EnableRemoteBanList = cfg.Bind(K, "启用在线名单", true,
                "从 ACE 公开端点获取共享封禁名单。" + NL +
                "请求在后台线程执行，不阻塞游戏主线程；" + NL +
                "获取失败（断网、端点不可用）时沿用上一次结果，不影响已有名单生效。");

            BanListEndpoint = cfg.Bind(K, "在线名单端点", BanListRemote.DefaultEndpoint,
                "返回 JSON 的 HTTP 端点。仅在需要指向自建服务时修改。");

            BanListWriteToken = cfg.Bind(K, "在线名单写入令牌", "",
                "留空时「封禁」只写入本地名单，不改动服务器上的共享名单。"
                + NL +
                "填上令牌后，本机封禁的人会推送到在线名单（所有人可见）。"
                + NL +
                "令牌是服务器上的写入凭据，只应填在维护者的机器上。");

            BanListRefreshHours = cfg.Bind(K, "在线名单刷新间隔（小时）", 6f,
                new ConfigDescription(
                    "两次拉取之间的最小间隔。名单变动频率很低，无需频繁请求。",
                    new AcceptableValueRange<float>(1f, 72f)));

            // 绑定全部完成后再迁移 —— 新键必须已经存在才能接住旧值。
            MigrateLegacyKeys(cfg);
        }

        // 预设只覆盖移动阈值；不更改检测开关、聊天规则、白名单或处罚权限。
        public void ApplyPreset(int preset)
        {
            if (preset < 0 || preset > 2) return;
            MaxSpeedTolerance.Value = preset == 0 ? 2f : preset == 1 ? 1.6f : 1.4f;
            TeleportMinDistance.Value = preset == 0 ? 6f : preset == 1 ? 4.5f : 4f;
            SpeedStrikeCount.Value = preset == 0 ? 5 : preset == 1 ? 3 : 2;
            RoundStartGracePeriod.Value = preset == 0 ? 6f : 4f;
            PositionJitterTolerance.Value = preset == 0 ? 0.5f : preset == 1 ? 0.35f : 0.3f;
        }

        public void RestoreSafeDefaults()
        {
            foreach (var field in typeof(AntiCheatConfig).GetFields())
            {
                // 用户自定义内容与处罚权限永远不由恢复默认覆盖。
                if (field.Name == nameof(AllowAutoKick) || field.Name == nameof(DispositionMode) ||
                    field.Name == nameof(ChatAbuseKeywords) || field.Name == nameof(AcePresenceTag) ||
                    field.Name == nameof(TrustedPluginGuids) || field.Name == nameof(TrustedPluginNames) ||
                    field.Name == nameof(RuleGroupMapping)) continue;
                if (field.GetValue(this) is ConfigEntryBase entry) entry.BoxedValue = entry.DefaultValue;
            }
        }

        public string[] GetTrustedGuids() => SplitList(TrustedPluginGuids.Value);
        public string[] GetTrustedNames() => SplitList(TrustedPluginNames.Value);

        private static string[] SplitList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return System.Array.Empty<string>();
            var parts = raw.Split(',');
            var result = new System.Collections.Generic.List<string>(parts.Length);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) result.Add(t);
            }
            return result.ToArray();
        }
    }
}
