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

        /// <summary>左上角是否显示帧率 / 延迟 / 房主状态条。</summary>
        public readonly ConfigEntry<bool> ShowStatsHud;

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

        public AntiCheatConfig(ConfigFile cfg)
        {
            File = cfg;

            // 任何一处改值立刻写盘，调用方不用记得手动 Save。
            File.SaveOnConfigSet = true;

            // ---------------- 开哪些检测 ----------------
            const string A = "开哪些检测";
            EnableStaticScan = cfg.Bind(A, "扫描作弊插件", true,
                "看看别人装了什么作弊插件。误判极少，建议一直开着。");
            EnableBehaviorScan = cfg.Bind(A, "检测瞬移和超速", true,
                "定时记录每个人的位置，看谁突然消失、或者跑得比正常人快。");
            EnableEventScan = cfg.Bind(A, "检查动作是否合法", true,
                "检查击杀、爬管道这些动作在当前状态下能不能做。");
            EnableWallClipCheck = cfg.Bind(A, "检测穿墙", false,
                "检查有没有人直接穿过墙跑。" + NL +
                "会额外吃一点性能，而且网络卡的时候容易误判。" + NL +
                "想认准作弊请用上面那几项，这项默认关着。");
            VerboseLogging = cfg.Bind(A, "输出详细日志", false,
                "把每一次检测的原始数据都记下来。" + NL +
                "只在怀疑误判、想查原因的时候才需要开，正常游玩请关着，日志会长得很快。");

            // ---------------- 多久看一次 ----------------
            const string B = "多久看一次位置";
            SampleInterval = cfg.Bind(B, "看位置的间隔（秒）", 0.10f,
                new ConfigDescription(
                    "每隔多久记录一次所有人的位置。" + NL +
                    "越小抓得越紧，但也越吃性能。" + NL +
                    "0.1 秒是推荐值，觉得卡就调大到 0.2。",
                    new AcceptableValueRange<float>(0.02f, 1.0f)));
            RoundStartGracePeriod = cfg.Bind(B, "开局后先不管几秒", 4.0f,
                new ConfigDescription(
                    "对局刚开始的那几秒大家都在传送，先不判定，避免误报。",
                    new AcceptableValueRange<float>(0f, 30f)));
            PositionJitterTolerance = cfg.Bind(B, "多小的位移算正常抖动", 0.35f,
                "小于这个距离的移动当成网络延迟带来的抖动，不算作弊。");

            // ---------------- 多快算超速 ----------------
            const string C = "怎么算跑太快";
            MaxSpeedTolerance = cfg.Bind(C, "允许比正常快几倍", 1.6f,
                new ConfigDescription(
                    "游戏里角色的正常速度是固定的，这里允许乘一个倍数当作上限，超出就算超速。" + NL +
                    "1.0 就是完全不放水，正常建议 1.5 ~ 1.8。",
                    new AcceptableValueRange<float>(1.0f, 5.0f)));
            TeleportMinDistance = cfg.Bind(C, "一下挪多远算瞬移", 4.5f,
                new ConfigDescription(
                    "一次记录里位置突然变了这么多，就是瞬移。" + NL +
                    "这个几乎不可能误判，因为正常走路一秒也走不了这么远。",
                    new AcceptableValueRange<float>(0.5f, 20f)));
            SpeedStrikeCount = cfg.Bind(C, "超速几次才记下来", 3,
                new ConfigDescription(
                    "偶尔超一下可能是卡了。连续超这么多次，才当成作弊证据。",
                    new AcceptableValueRange<int>(1, 20)));

            // ---------------- 多离谱算动作违规 ----------------
            const string D = "怎么算动作违规";
            KillDistanceTolerance = cfg.Bind(D, "隔多远能砍人（额外放宽）", 0.75f,
                new ConfigDescription(
                    "游戏设置里的击杀距离之外，再放宽这么多。" + NL +
                    "用来排除视野边缘打不到的情况，调太小会漏掉远程击杀挂。",
                    new AcceptableValueRange<float>(0f, 5f)));
            KillCooldownTolerance = cfg.Bind(D, "冷却能提前多久（秒）", 0.35f,
                new ConfigDescription(
                    "内鬼的杀人冷却是 25 秒。能提前这么多秒再杀，就是绕过冷却。",
                    new AcceptableValueRange<float>(0f, 5f)));
            TaskSpeedTolerance = cfg.Bind(D, "做任务允许快几倍", 1.5f,
                new ConfigDescription(
                    "两个任务点之间允许花的最短时间 = 正常时间 除以 这个倍数。" + NL +
                    "调太小会把边走边做任务的正常玩家误判成外挂。",
                    new AcceptableValueRange<float>(1.0f, 5f)));
            RemoteTaskTolerance = cfg.Bind(D, "隔多远能交任务（额外放宽）", 2.0f,
                new ConfigDescription(
                    "站在任务点附近多少距离之内算完成。",
                    new AcceptableValueRange<float>(0f, 10f)));
            MeetingMoveTolerance = cfg.Bind(D, "会议时允许走多远", 0.75f,
                new ConfigDescription(
                    "开会期间所有人都该站在会议桌附近，走太远就是有问题。",
                    new AcceptableValueRange<float>(0f, 5f)));

            // ---------------- 抓到之后怎么办 ----------------
            const string E = "抓到之后怎么办";
            DispositionMode = cfg.Bind(E, "动手的方式", DispositionModes.Warn,
                new ConfigDescription(
                    "抓到作弊的人之后怎么办：" + NL +
                    "  忽略 — 只写进记录，不做任何动作" + NL +
                    "  警告 — 在屏幕上提示你" + NL +
                    "  踢出 — 把他踢出房间（需要你是房主）" + NL +
                    "  封禁 — 踢出并加入本地黑名单（需要你是房主）" + NL +
                    "判定方式是「规则命中」：命中规则就记一笔，命中确定性规则立即处置，" +
                    "不再靠分数累计，所以没有「多少分」这种设置。",
                    new AcceptableValueList<string>(DispositionModes.All)));
            AllowAutoKick = cfg.Bind(E, "自动踢人（不用手动点）", false,
                "命中确定性规则就自动踢，不用你点确认。" + NL +
                "默认关着——先只记录，确认没误判了再开。");

            // ---------------- 界面 ----------------
            const string F = "界面显示";
            ShowDesktopSplash = cfg.Bind(F, "显示开机启动动画", true,
                "进游戏时在桌面右下角弹一下 Apex Cheat Ender 的加载动画。");
            ShowNotifications = cfg.Bind(F, "屏幕顶部弹出提醒", true,
                "命中检测规则时在屏幕上方弹一条通知。");
            NotificationDuration = cfg.Bind(F, "提醒停留几秒", 5f,
                new ConfigDescription(
                    "通知自动消失的时间。",
                    new AcceptableValueRange<float>(1f, 20f)));

            ShowChatAbuseNotice = cfg.Bind(F, "疑似骂人短提示", true,
                "仅聊天匹配关键词时在本地游戏 Canvas 显示图片 1.2 秒，Esc 关闭，10 秒冷却。" + NL +
                "关键词无法理解语境，引用或讨论也可能误报；只标为疑似，不计作弊证据，不踢人，可随时关闭。");
            ChatAbuseKeywords = cfg.Bind(F, "疑似骂人关键词", ChatAbuseNoticePolicy.DefaultKeywords,
                "自定义关键词，用逗号分隔；留空不提示。仅直接子串匹配，不推断语境。" + NL +
                "避免使用宽泛单字以减少误报；不影响聊天发送或原有反作弊功能。");

            ShowRepeatedChatNotice = cfg.Bind(F, "重复聊天本地提示", false,
                "同一玩家10秒内重复相同消息时仅本地提醒，不拦截聊天、不生成作弊证据。");
            RepeatedChatThreshold = cfg.Bind(F, "重复几次才提示", 3,
                new ConfigDescription("10秒内相同消息达到此次数才提示，10秒冷却。", new AcceptableValueRange<int>(3, 10)));
            RuleGroupMapping = cfg.Bind("本地规则分组", "规则到分组映射", RuleGroups.Defaults(),
                "格式：Teleport=移动;IllegalChat=消息。仅影响本地历史分类，不影响检测和处置。" + NL +
                "允许分组：移动、动作、会议、消息、网络、静态。非法映射回退内置分组，不执行代码、不联网。");

            // ---------------- 同装 ACE 的玩家 ----------------
            AcePresenceEnabled = cfg.Bind(F, "标记同装 ACE 的玩家", true,
                "跟同样装了 Apex Cheat Ender 的人互相认一下，" + NL +
                "在对方名字后面加个标记，方便看出谁是自己人。" + NL +
                "原理是发一条游戏不用的网络消息，对方也装了就会回。");
            AcePresenceTag = cfg.Bind(F, "标记写成什么", Core.PresenceTags.AceDefault,
                "显示在对方名字后面的文字。" + NL +
                "默认带两个表情符号；如果游戏里显示成方块，" + NL +
                "改成纯文字（比如 [ACE]）即可。");

            AmethystPresenceEnabled = cfg.Bind(F, "标记装了 Amethyst 的玩家", true,
                "Amethyst（amethyst.mod）也是 Among Us 的客户端模组，" + NL +
                "它自己会周期性广播一条识别包。这里只**旁听**那条包，" + NL +
                "认出谁装了 Amethyst，在名字后面加标记。" + NL +
                "不会给 Amethyst 发任何东西，也不会拦它的包。");
            AmethystPresenceTag = cfg.Bind(F, "Amethyst 标记写成什么", Core.PresenceTags.AmethystDefault,
                "显示在 Amethyst 用户名字后面的文字。");

            ShowStatsHud = cfg.Bind(F, "显示帧率延迟房主", true,
                "屏幕左上角显示一行小字：当前帧率、与服务器的延迟、" + NL +
                "以及自己是房主还是普通客户端。" + NL +
                "只在连上房间后显示；延迟超过 100 毫秒变黄、超过 200 毫秒变红。");

            // ---------------- 爬管道 / 滑索 ----------------
            const string G = "爬管道和滑索";
            VentNonImpostor = cfg.Bind(G, "抓普通人爬管道", true,
                "只有内鬼能爬管道。其他人爬了就是开了挂。");
            VentRemote = cfg.Bind(G, "抓隔着屏幕爬管道", true,
                "离管道口很远却爬进去了。");
            VentForgedId = cfg.Bind(G, "抓伪造管道编号", true,
                "发了一个根本不存在的管道编号，说明在改游戏数据。");
            VentForceOther = cfg.Bind(G, "抓强迫别人爬管道", true,
                "用漏洞让别人被强行拉进管道。");
            ZiplineAbuse = cfg.Bind(G, "抓滑索滥用", true,
                "强行滑索，或者开会的时候滑索。");
            VentDuringMeeting = cfg.Bind(G, "抓开会时爬管道", true,
                "开会期间所有人都被定在会议桌，这时候不可能爬管道。");
            VentDistanceTolerance = cfg.Bind(G, "爬管道允许离多远", 2.0f,
                new ConfigDescription(
                    "离管道口多远之内算正常使用。太严格会误判站在旁边的人。",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            // ---------------- 记录 ----------------
            const string H = "记录";
            RecordPlayerHistory = cfg.Bind(H, "记录谁进过房间", true,
                "记下每次开局都有谁，写进 PlayerHistory.txt，方便事后查。");
            RecordCheatHistory = cfg.Bind(H, "记录作弊判定", true,
                "每次命中都写进 CheatHistory.txt，含昵称和命中的具体规则。" + NL +
                "如果有人被误判了，这里就是翻案证据。");

            // ---------------- 网络防护 ----------------
            const string I = "网络防护";
            RpcFloodDetection = cfg.Bind(I, "抓数据包刷屏", true,
                "有人疯狂发数据包会让全房卡顿。");
            RpcRateLimit = cfg.Bind(I, "10 秒内最多几次数据包", 60,
                new ConfigDescription(
                    "一个人 10 秒内发这么多数据包，就当成在刷屏。" + NL +
                    "网络差的房间可能会误判，遇到误报就往上调。",
                    new AcceptableValueRange<int>(5, 200)));
            SnapRateDetection = cfg.Bind(I, "抓瞬移式位置同步", true,
                "短时间内反复强制同步位置，是瞬移挂的典型做法。");
            SnapRateLimit = cfg.Bind(I, "10 秒内最多几次强制同步", 6,
                new ConfigDescription(
                    "正常对局里几乎不会出现连续的位置强制同步。",
                    new AcceptableValueRange<int>(1, 40)));
            BlockEarlyMeeting = cfg.Bind(I, "抓开局乱开会", true,
                "开局几秒内疯狂开会举报的，通常是在刷屏或者想破坏游戏。");
            EarlyMeetingGrace = cfg.Bind(I, "开局后几秒内不许开会", 15f,
                new ConfigDescription(
                    "开局保护时间。这段时间内开会举报会被拦下。",
                    new AcceptableValueRange<float>(0f, 60f)));
            OversizedPacketCheck = cfg.Bind(I, "抓超大数据包", true,
                "异常大的数据包，可能是想拖垮所有人。");

            // ---------------- 进阶检测 ----------------
            const string L = "进阶检测";
            SabotageCheck = cfg.Bind(L, "抓非法破坏", true,
                "破坏系统只有内鬼能触发，且会议期间不该发生。" + NL +
                "这项抓的是：非内鬼破坏、会议中破坏、以及越界/无效的破坏目标。");
            RoleActionCheck = cfg.Bind(L, "抓角色动作异常", true,
                "变形只有变形者能用，保护只有守护天使能用。" + NL +
                "拿不到角色信息时会放行，不会误伤。");
            ChatCheck = cfg.Bind(L, "抓聊天刷屏和非法消息", true,
                "短时间内疯狂发消息会把聊天框刷爆，也可能是在卡别人。" + NL +
                "同时拦截空消息、超长消息和含控制字符的消息。");
            ChatRateLimit = cfg.Bind(L, "10 秒内最多几条消息", 8,
                new ConfigDescription(
                    "超过这个数量就算刷屏。" + NL +
                    "正常聊天很难达到 8 条，遇到误报可以往上调。",
                    new AcceptableValueRange<int>(3, 50)));
            NameCheck = cfg.Bind(L, "抓非法昵称", true,
                "空昵称、超长昵称、含换行或控制字符的昵称都会被抓。" + NL +
                "有些外挂用超长昵称撑爆别人的聊天框。");
            NameMaxLength = cfg.Bind(L, "昵称最长多少字", 20,
                new ConfigDescription(
                    "超过这个长度就算异常。游戏原生上限是 10，这里留了余量。",
                    new AcceptableValueRange<int>(5, 60)));

            // ---------------- 性能 ----------------
            const string M = "性能";
            PerfDontUpdateDead = cfg.Bind(M, "不更新死亡玩家", true,
                "死了的人不用再算走位，省下来的开销很可观。" + NL +
                "被杀的瞬间仍会做一次判定，不影响检测准确性。");
            PerfDeadSkipFrames = cfg.Bind(M, "死亡玩家跳几帧才算一次", 5,
                new ConfigDescription(
                    "死亡玩家每隔这么多次采样才处理一次。" + NL +
                    "调到 1 就是每次都算（最费），调大更省。" + NL +
                    "只有在「不更新死亡玩家」开着时才生效。",
                    new AcceptableValueRange<int>(1, 30)));
            PerfLowLoad = cfg.Bind(M, "低负载模式", false,
                "整体把检测频率降下来，换来更稳的帧率。" + NL +
                "机器差、或者人多的房间卡顿时打开它。" + NL +
                "代价是抓瞬移的灵敏度会下降（采样间隔变长）。");
            PerfProbe = cfg.Bind(M, "性能探针（排查卡顿用）", false,
                "记录本模组自己每帧花了多少时间，超过阈值就写日志。" + NL +
                "怀疑是插件导致掉帧时打开它，日志里搜 [性能探针]。" + NL +
                "平时请关着。");

            // ---------------- 白名单 ----------------
            const string J = "白名单";            TrustedPluginGuids = cfg.Bind(J, "信任的插件 GUID", "",
                "用英文逗号隔开。列在这里的插件就算被误判也不会拦。一般不用填。");
            TrustedPluginNames = cfg.Bind(J, "信任的插件名", "",
                "用英文逗号隔开。按插件名字匹配，认不出 GUID 时用这个。一般不用填。");

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
