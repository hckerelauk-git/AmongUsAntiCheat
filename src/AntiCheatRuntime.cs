using System.Collections.Generic;
using ApexCheatEnder.Config;
using ApexCheatEnder.Core;
using BepInEx.Logging;
using UnityEngine;

namespace ApexCheatEnder
{
    /// <summary>
    /// 反作弊的纯托管运行时。
    ///
    /// 为什么所有逻辑都放在这里，而不是放在 MonoBehaviour 里？
    ///
    /// 因为注册进 il2cpp 域的 MonoBehaviour，其**每一个方法（含私有）**都会被
    /// Il2CppInterop 尝试桥接给 il2cpp。只要签名里出现 il2cpp 不认识的托管类型
    /// （比如 VerdictEngine、Violation），它就会刷 "unsupported return type" 警告，
    /// 严重时直接导致类型注册失败、整个插件加载不起来。
    ///
    /// 所以架构是：MonoBehaviour 只保留一层空壳（转发 Unity 消息），
    /// 全部状态与逻辑都在这个不注册进 il2cpp 的普通静态类里。
    /// </summary>
    internal static class AntiCheatRuntime
    {
        // ================= 依赖 =================

        public static AntiCheatConfig Config { get; private set; }
        public static ManualLogSource Log { get; private set; }
        public static PlayerTracker Tracker { get; private set; }
        public static BehaviorAnalyzer Analyzer { get; private set; }
        public static VerdictEngine Verdicts { get; private set; }
        public static ModScanner Scanner { get; private set; }

        /// <summary>RPC 事件采集器（用于事后追溯）。</summary>
        public static Core.RpcEventRecorder Recorder { get; private set; }

        /// <summary>配置文件热重载器：记事本改完保存后几秒内自动生效，不用重启。</summary>
        public static Core.ConfigHotReloader HotReloader { get; private set; }
        public static Core.Rpc.RpcFloodGuard RpcFlood { get; private set; }

        /// <summary>静态扫描报告，供面板展示。</summary>
        public static ModScanner.ScanReport ScanReport { get; set; }

        public static bool IsReady =>
            Config != null && Verdicts != null && Tracker != null && Analyzer != null && Scanner != null;

        // ================= 循环状态 =================

        private static float _nextSampleTime;
        private static float _roundStartTime;
        private static bool _wasInGame;

        /// <summary>上一帧的对局状态，仅用于识别「刚离开对局」这个边沿。</summary>
        private static bool _wasInGameAtPresence;
        private static bool _wasInMeeting;

        /// <summary>
        /// 风险等级巡检间隔（秒）。
        /// 每帧遍历全部玩家判定并调用 EvaluateLevel 是不必要的开销，
        /// 通知本身又是给人看的，5Hz 足够。
        /// </summary>
        private const float RiskCheckInterval = 0.2f;
        private static float _nextRiskCheckTime;

        /// <summary>封禁名单检查间隔。好友码不会中途变，1 秒一次足够。</summary>
        private const float BanCheckInterval = 1.0f;

        private static float _nextBanCheckTime;

        /// <summary>
        /// 本局已经报过的玩家。
        ///
        /// 名单检查每秒跑一次，没有这个集合就会**每秒提交一条重复证据** ——
        /// 通知会被刷屏，证据条数也会虚高到毫无意义。
        /// </summary>
        private static readonly HashSet<int> BanReported = new HashSet<int>();

        /// <summary>
        /// 本局已经**查过**的玩家（命中与否都记）。
        ///
        /// 好友码不会在对局中途变，所以每人每局查一次就够。
        /// 没有这个集合的话，每秒都要给每个人做一次字符串归一化 ——
        /// 15 名玩家即每秒数十次分配，属纯开销。
        /// </summary>
        private static readonly HashSet<int> BanChecked = new HashSet<int>();

        /// <summary>解析好的自定义名单，以及它对应的原始配置串（串没变就不重解析）。</summary>
        private static List<BanEntry> _banExtra;
        private static string _banExtraRaw;

        /// <summary>复用的玩家缓冲区：避免每次采样都分配一个列表。</summary>
        private static readonly List<PlayerControl> PlayerBuffer = new List<PlayerControl>(16);

        /// <summary>采样轮次计数，用于死亡玩家跳帧。</summary>
        private static int _sampleTick;

        // ================= 性能探针 =================
        // 用途：当用户怀疑「装了插件就掉帧」时，能拿出本模组自身的真实开销，
        // 而不是靠猜。只在配置打开时才计时，避免探针本身成为负担。

        private static readonly System.Diagnostics.Stopwatch PerfWatch = new System.Diagnostics.Stopwatch();
        private static double _perfAccumMs;
        private static int _perfFrames;
        private static float _fpsElapsed;
        private static int _fpsFrames;
        public static float CurrentFps { get; private set; }
        public static double AverageTickMs { get; private set; }

        public static string ExportDiagnostics()
        {
            try
            {
                var directory = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "ACELogs");
                System.IO.Directory.CreateDirectory(directory);
                var path = System.IO.Path.Combine(directory, "diagnostics-" + System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".txt");
                System.IO.File.WriteAllText(path, Verdicts.History.SafeExport(CurrentFps), System.Text.Encoding.UTF8);
                return "已导出到 BepInEx/ACELogs";
            }
            catch { return "导出失败，请检查日志目录权限"; }
        }
        private static float _nextPerfReportTime;

        /// <summary>单帧开销告警阈值（毫秒）。超过它说明本模组在拖后腿。</summary>
        private const double PerfWarnMs = 2.0;

        /// <summary>复用的证据缓冲，避免每帧分配列表。</summary>
        private static readonly List<Violation> EvidenceBuffer = new List<Violation>(16);

        public static void Initialize(
            AntiCheatConfig config,
            ManualLogSource log,
            PlayerTracker tracker,
            BehaviorAnalyzer analyzer,
            VerdictEngine verdicts,
            ModScanner scanner)
        {
            Config = config;
            Log = log;
            Tracker = tracker;
            Analyzer = analyzer;
            Verdicts = verdicts;
            Scanner = scanner;

            Recorder = new Core.RpcEventRecorder();
            RpcFlood = new Core.Rpc.RpcFloodGuard(log);

            // 热重载器：监视配置文件外部改动，改完保存自动生效。
            HotReloader = new Core.ConfigHotReloader(
                config,
                OnConfigFileChanged,
                (section, msg) => log?.LogInfo("[" + section + "] " + msg));
            HotReloader.Prime();
        }

        /// <summary>
        /// 用户在游戏外用记事本改了配置并保存后，热重载器检测到并 Diff 出变更，回调到这里。
        /// 目前所有阈值参数都是实时读取的，所以这里主要做两件事：
        ///   1. 同步事件检测开关的快照（补丁层读的是这个快照，不是配置本身）
        ///   2. 把变更弹一条通知，让用户知道改动已经生效
        /// </summary>
        private static void OnConfigFileChanged(IReadOnlyList<ConfigChange> changes)
        {
            if (Config != null)
                AntiCheatPlugin.EventScanEnabled = Config.EnableEventScan.Value;

            if (changes == null || changes.Count == 0) return;

            // 仅弹出第一条，避免批量修改时刷屏
            var first = changes[0];
            var extra = changes.Count > 1 ? "（共 " + changes.Count + " 项）" : string.Empty;

            try
            {
                UI.NotificationPanel.Show(
                    "设置已更新",
                    first.Key + "：" + first.OldValue + " → " + first.NewValue + extra,
                    UI.AceTheme.Accent,
                    3.5f);
            }
            catch { }
        }

        // ================= 供补丁层调用的入口 =================

        /// <summary>当前游戏时间。</summary>
        public static float Now => Time.time;

        /// <summary>
        /// 本回合开始的时刻（<see cref="Time.time"/>）。
        /// 用于「开局保护期」类判定（如早会检测）。
        /// 尚未进入过对局时返回 0，此时 <c>Now - RoundStartTime</c> 会是一个很大的值，
        /// 不会误触发保护期逻辑。
        /// </summary>
        public static float RoundStartTime => _roundStartTime;

        /// <summary>补丁层提交证据的统一入口。</summary>
        public static void Submit(Violation violation)
        {
            Verdicts?.Submit(violation, Time.time);
        }

        /// <summary>补丁层按玩家 Id 取轨迹（不存在则创建）。</summary>
        public static PlayerTrack GetOrCreateTrack(int playerId, string name)
        {
            return Tracker?.GetOrCreate(playerId, name, Time.time);
        }

        /// <summary>
        /// 设置界面改动了配置项后调用。
        /// 阈值类参数都是实时读取的，所以这里只做记录；
        /// 需要重建缓存的项（如白名单）后续在此处统一处理。
        /// </summary>
        public static void ApplyConfigChange()
        {
            // 显式写回磁盘：BepInEx 不会在每次 Value 变更时自动落盘，
            // 不 Save 的话界面上改的设置在重启游戏后会丢失。
            try { Config?.File?.Save(); }
            catch { /* 文件被占用等情况下忽略，不影响运行 */ }

            Log?.LogInfo("[配置] 设置已更新并保存。");
        }

        // ================= 生命周期（由帧驱动补丁调用） =================

        /// <summary>插件加载完成后调用一次：接线各模块。</summary>
        public static void OnLoaded()
        {
            if (!IsReady)
            {
                Log?.LogError("[反作弊] 运行时未初始化。");
                return;
            }

            Analyzer.IsInsideWall = GameBridge.IsInsideWall;
            Verdicts.KickRequested += HandleKickRequested;

            Log?.LogInfo("[反作弊] 运行时已接线，等待帧驱动。");
        }

        /// <summary>插件卸载时调用。</summary>
        public static void Shutdown()
        {
            if (Verdicts != null) Verdicts.KickRequested -= HandleKickRequested;
            UI.MainMenuArt.Shutdown();
            UI.ChatAbuseNotice.Shutdown();
        }

        /// <summary>
        /// 重复提示的最小间隔（秒）。
        ///
        /// 同一个玩家在等级不变的情况下又犯了新规，等这么久再弹一次。
        /// 太短会刷屏，太长又回到「只爆一次」的老毛病。
        /// </summary>
        private const float NotificationRepeatInterval = 6f;

        /// <summary>
        /// 检查每个玩家的判定等级，需要时弹右下角通知。
        ///
        /// 触发条件（满足其一）：
        ///   · **等级升级** —— 首次发现某人作弊，或风险从可疑升到高风险
        ///   · **又有新证据** 且距上次提示已超过 <see cref="NotificationRepeatInterval"/>
        ///
        /// 为什么不能只看「等级升级」：
        /// 一个玩家一旦升到高风险，之后所有作弊都还是高风险，等级不再变化，
        /// 通知将不再弹出 —— 表现为「仅提示一次，后续作弊不再告警」。
        /// 加上第二条后，持续作弊的人会每隔几秒再提示一次，同时不会刷屏。
        /// </summary>
        private static void CheckRiskLevelChanges()
        {
            var cfg = Config;
            var verdicts = Verdicts;
            if (cfg == null || verdicts == null) return;

            var now = Time.time;

            foreach (var v in verdicts.Verdicts.Values)
            {
                var level = v.EvaluateLevel();

                if (level <= RiskLevel.Normal)
                {
                    // 回到正常：重置标记，允许下次重新提示
                    v.LastNotifiedLevel = RiskLevel.Normal;
                    v.LastNotifiedEvidenceCount = 0;
                    continue;
                }

                var escalated = level > v.LastNotifiedLevel;
                var hasNewEvidence = v.Evidence.Count > v.LastNotifiedEvidenceCount;
                var cooledDown = now - v.LastNotifyTime >= NotificationRepeatInterval;

                // 升级必弹；等级没变则要「有新证据 + 冷却已过」才弹。
                // 判定本身抽在 PlayerVerdict.ShouldNotify 里，方便脱离 Unity 回归测试。
                if (!PlayerVerdict.ShouldNotify(level, v.LastNotifiedLevel,
                        v.Evidence.Count, v.LastNotifiedEvidenceCount,
                        now, v.LastNotifyTime, NotificationRepeatInterval))
                    continue;

                v.LastNotifiedLevel = level;
                v.LastNotifiedEvidenceCount = v.Evidence.Count;
                v.LastNotifyTime = now;

                // 用户关掉了通知就只记日志，不弹卡片
                if (!(cfg.ShowNotifications?.Value ?? true)) continue;

                var reason = v.Evidence.Count > 0
                    ? v.Evidence[v.Evidence.Count - 1].Detail
                    : "行为异常";

                UI.NotificationPanel.Show(
                    UI.NotificationPanel.TitleOf(level),
                    $"{v.Name} —— {reason}",
                    UI.NotificationPanel.AccentOf(level),
                    cfg.NotificationDuration.Value);
            }
        }

        public static void Tick(float now, float deltaTime)
        {
            // 分辨率自愈放在最前面，且不受 IsReady 限制：
            // 窗口被压成 160×40 时，先让画面能用比什么都重要。
            ResolutionGuard.Tick();

            if (!IsReady) return;

            _fpsElapsed += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (_fpsElapsed >= 1f)
            {
                CurrentFps = _fpsFrames / _fpsElapsed;
                _fpsElapsed = 0f;
                _fpsFrames = 0;
            }
            // 性能探针：只统计本模组自身的开销，不含游戏逻辑
            var probing = Config?.PerfProbe?.Value ?? false;
            if (probing) PerfWatch.Restart();

            try
            {
                TickCore(now, deltaTime);
            }
            finally
            {
                if (probing) ReportPerf(now);
            }
        }

        /// <summary>累计并定期上报本模组的每帧开销。</summary>
        private static void ReportPerf(float now)
        {
            PerfWatch.Stop();
            _perfAccumMs += PerfWatch.Elapsed.TotalMilliseconds;
            _perfFrames++;

            if (now < _nextPerfReportTime) return;
            _nextPerfReportTime = now + 5f;

            if (_perfFrames == 0) return;
            var avg = _perfAccumMs / _perfFrames;
            AverageTickMs = avg;
            _perfAccumMs = 0;
            _perfFrames = 0;

            if (avg >= PerfWarnMs)
                Log?.LogWarning($"[性能探针] 检测循环平均每帧 {avg:F2} ms，"
                              + $"超过阈值 {PerfWarnMs:F1} ms（追踪玩家 {Tracker?.Tracks.Count ?? 0} 人）。"
                              + "考虑打开「低负载模式」。");
            else if (Config?.VerboseLogging?.Value ?? false)
                Log?.LogInfo($"[性能探针] 检测循环平均每帧 {avg:F3} ms。");
        }

        private static void TickCore(float now, float deltaTime)
        {
            if (!IsReady) return;

            // 先跑热重载检测：用户可能在游戏外改了配置文件。
            HotReloader?.Tick(now);

            var inGame = GameBridge.IsInGame;

            // 边沿：刚进入对局
            if (inGame && !_wasInGame) OnRoundEntered(now);

            // 边沿：刚退出对局
            if (!inGame && _wasInGame) OnRoundExited();

            _wasInGame = inGame;

            // 会议边沿
            var inMeeting = inGame && GameBridge.IsInMeeting;
            if (inMeeting && !_wasInMeeting) OnMeetingStarted(now);
            if (!inMeeting && _wasInMeeting) OnMeetingEnded(now);
            _wasInMeeting = inMeeting;

            // ACE 客户端互认：周期性广播握手包，认出房间里同装本插件的人。
            // 放在这里而不是 UI 层——它属于逻辑，UI 未创建时也该正常工作。
            AcePresence.Tick();

            // 离开对局时清空 Amethyst 识别结果：PlayerId 每局重新分配，
            // 留着会把上一局的人认成本局的。
            if (!inGame && _wasInGameAtPresence)
            {
                AmethystPresence.Reset();
                ModFingerprint.Reset();
            }
            _wasInGameAtPresence = inGame;

            // 身份摘要：命中时记下好友码 / 平台 ID —— 没有它就无法封禁。
            if (VerdictEngine.DescribeIdentity == null)
                VerdictEngine.DescribeIdentity = id =>
                {
                    try
                    {
                        var p = GameBridge.GetPlayerById(id);
                        if (p == null) return string.Empty;

                        var code = GameBridge.GetFriendCode(p);
                        var puid = GameBridge.GetPuid(p);

                        var sb = new System.Text.StringBuilder();
                        if (code.Length > 0) sb.Append(" 好友码=").Append(code);
                        if (puid.Length > 0) sb.Append(" 平台ID=").Append(puid);

                        // 模组指纹：把对方装了哪些模组一起记下来。
                        // 「他开的是什么挂」这类问题，答案就在这里 ——
                        // 指纹是按自定义 RPC 的 callId 被动观察到的，对方躲不掉。
                        try
                        {
                            var mods = Core.ModFingerprint.Of(id);
                            if (mods != null && mods.Count > 0)
                            {
                                var names = new System.Collections.Generic.List<string>(mods.Count);
                                foreach (var m in mods)
                                    if (m != null && !string.IsNullOrEmpty(m.Display)) names.Add(m.Display);

                                if (names.Count > 0)
                                    sb.Append(" 模组=").Append(string.Join("+", names));
                            }
                        }
                        catch { }

                        return sb.ToString();
                    }
                    catch { return string.Empty; }
                };

            // 位移诊断：按玩家号取状态摘要。核心层不引用 GameBridge，由这里注入。
            if (Analyzer != null && BehaviorAnalyzer.DescribeState == null)
                BehaviorAnalyzer.DescribeState = id =>
                {
                    try
                    {
                        var p = GameBridge.GetPlayerById(id);
                        return p == null ? "<未找到玩家>" : GameBridge.DescribeMotionState(p);
                    }
                    catch { return "<读取失败>"; }
                };

            // 核心层不引用运行时，日志钩子在这里安装。
            if (BanListRemote.LogInfo == null)
            {
                BanListRemote.LogInfo = m => Log?.LogInfo(m);
                BanListRemote.LogWarning = m => Log?.LogWarning(m);
            }

            // 在线名单：**必须放在「是否在对局中」判断之前**。
            // 放在 CheckBanList 里会导致只有进对局才拉取，而玩家通常先进大厅再进房，
            // 名单永远来不及准备好 —— 表现为「功能未生效」，但日志里一条错都没有。
            BanListRemote.Tick(now,
                Config.EnableRemoteBanList.Value,
                Config.BanListEndpoint.Value,
                Config.BanListRefreshHours.Value * 60f);

            if (!inGame)
            {
                // 不在对局中也要驱动，保持调用方契约
                Verdicts.Tick(now, deltaTime);
                return;
            }

            // 采样（低负载模式下把间隔拉长一倍，用灵敏度换帧率）
            if (Config.EnableBehaviorScan.Value && now >= _nextSampleTime)
            {
                var interval = Config.SampleInterval.Value;
                if (Config.PerfLowLoad.Value) interval *= 2f;
                _nextSampleTime = now + interval;
                SampleAllPlayers(now, inMeeting);
            }

            // 封禁名单：只看身份、不看行为，所以和采样分开走
            CheckBanList(now);

            // 衰减
            Verdicts.Tick(now, deltaTime);

            // 判定等级升级 → 右下角弹通知（节流到 5Hz，避免每帧全量遍历）
            if (now >= _nextRiskCheckTime)
            {
                _nextRiskCheckTime = now + RiskCheckInterval;
                CheckRiskLevelChanges();
            }
        }

        // ================= 封禁名单 =================

        /// <summary>
        /// 对照封禁名单检查房间里每个人。
        ///
        /// 与其它检测最大的不同：**不看行为、只看身份** ——
        /// 命中依据是好友码 / 平台 ID（改名甩不掉），而不是名字。
        ///
        /// 命中后提交一条确定级证据，之后的警告 / 踢出 / 封禁**全部沿用现有处置设置**，
        /// 不另开一套逻辑。这样「封禁名单」就只是又一个证据来源，
        /// 处置规则只有一处，不会出现两套规则打架。
        /// </summary>
        private static void CheckBanList(float now)
        {
            if (!Config.EnableBanList.Value) return;
            if (now < _nextBanCheckTime) return;
            _nextBanCheckTime = now + BanCheckInterval;

            try
            {
                // 配置串变了才重新解析 —— 每秒解析一次字符串是纯浪费
                var raw = Config.BanListExtra.Value ?? string.Empty;
                if (!System.String.Equals(raw, _banExtraRaw, System.StringComparison.Ordinal))
                {
                    _banExtraRaw = raw;
                    _banExtra = BanListDb.Parse(raw);
                    // 名单变了要重查一遍，否则新加的条目对本局已查过的人不生效
                    BanChecked.Clear();
                    if (_banExtra.Count > 0)
                        Log?.LogInfo("[封禁名单] 已载入自定义条目 " + _banExtra.Count + " 条。");
                }

                GameBridge.GetPlayersInto(PlayerBuffer);

                for (var i = 0; i < PlayerBuffer.Count; i++)
                {
                    var player = PlayerBuffer[i];
                    if (player == null) continue;

                    var id = GameBridge.GetPlayerId(player);
                    if (id < 0 || BanChecked.Contains(id)) continue;

                    // 先记「查过」，再决定要不要跳过 ——
                    // 否则被跳过的人下一秒又会被重新归一化一遍。
                    BanChecked.Add(id);

                    // **绝不判自己。**
                    // 名单里有「鸟（繁体）」这种只有名字的条目，万一自己名字里带了同样的词，
                    // 将导致对自己执行警告或踢出。
                    if (player == GameBridge.GetLocalPlayer()) continue;

                    var name = GameBridge.GetPlayerName(player);
                    var code = GameBridge.GetFriendCode(player);
                    var puid = GameBridge.GetPuid(player);

                    if (!BanListDb.Check(code, puid, name, _banExtra, BanListRemote.Entries, out var hit)) continue;
                    if (BanReported.Contains(id)) continue;

                    BanReported.Add(id);

                    var detail = "命中封禁名单「" + hit.Name + "」" +
                                 (string.IsNullOrEmpty(hit.Code) ? string.Empty : "（好友码 " + hit.Code + "）") +
                                 (string.IsNullOrEmpty(hit.Puid) ? string.Empty : "（平台ID " + hit.Puid + "）") +
                                 "：" + hit.Reason;

                    Verdicts.Submit(new Violation(
                        ViolationKind.BannedPlayer,
                        Severity.Critical,
                        id,
                        name,
                        now,
                        detail), now);

                    Log?.LogWarning("[封禁名单] " + name + "（id=" + id + "）命中「" + hit.Name + "」：" + hit.Reason);
                }
            }
            catch (System.Exception ex)
            {
                if (Config.VerboseLogging.Value)
                    Log?.LogWarning("[封禁名单] 检查失败：" + ex.Message);
            }
        }

        // ================= 采样 =================

        private static void SampleAllPlayers(float now, bool inMeeting)
        {
            // 复用缓冲区：热路径不分配新列表
            GameBridge.GetPlayersInto(PlayerBuffer);
            if (PlayerBuffer.Count == 0) return;

            _sampleTick++;

            // 兜底值：拿本机玩家算。下面每个玩家会优先用「他自己」的理论速度。
            var fallbackMaxSpeed = GameBridge.GetMaxAllowedSpeed() * Config.MaxSpeedTolerance.Value;
            var tolerance = Config.MaxSpeedTolerance.Value;
            var grace = Config.RoundStartGracePeriod.Value;

            // 死亡玩家降频：死人不会再瞬移/超速，没必要每轮都做全套采样。
            // 这是移植自 Amethyst 的「不更新死亡玩家 + 跳帧」策略。
            var skipDead = Config.PerfDontUpdateDead.Value;
            var deadEvery = System.Math.Max(1, Config.PerfDeadSkipFrames.Value);
            var skipThisTick = deadEvery > 1 && (_sampleTick % deadEvery) != 0;

            var teleportThreshold = Config.TeleportMinDistance.Value;

            // ══════════ 第一遍：推快照，并统计本帧有多少人发生「大位移」 ══════════
            //
            // 会议开始、回合开始这类**场景级传送**会把所有人一起挪走，那不是任何一个人的行为。
            // 而且这类传送比游戏状态**早一帧** —— 实测诊断日志：
            //   inVent=0 onLadder=0 inMovingPlat=0 dead=0 inMeeting=0
            //   紧接着下一行才是「会议开始，已对所有玩家开启位置豁免窗口」
            // 于是整桌人都可能被逐个判成瞬移。
            //
            // 逐人判定看不出这种情况，必须在批次层面看：一帧里多人同时大位移 = 场景传送。
            var bigMovers = 0;

            foreach (var player in PlayerBuffer)
            {
                if (player == null) continue;

                var playerId = GameBridge.GetPlayerId(player);
                if (playerId < 0) continue;

                var isDead = GameBridge.IsDead(player);
                if (skipDead && isDead && skipThisTick) continue;

                var track = Tracker.GetOrCreate(playerId, GameBridge.GetPlayerName(player), now);

                // 角色能力：用 Role.CanVent 而不是阵营，避免把 Viper 这类
                // 「船员阵营但能钻管道」的角色误判成作弊。
                var roleKnown = GameBridge.TryGetCanVent(player, out var canVent);

                track.Push(new PlayerSnapshot
                {
                    Time = now,
                    Position = GameBridge.GetPosition(player),
                    IsDead = isDead,
                    InVent = GameBridge.IsInVent(player),
                    InSpecialMovement = GameBridge.IsInSpecialMovement(player),
                    IsImpostor = GameBridge.IsImpostor(player),
                    CanVent = canVent,
                    RoleKnown = roleKnown,
                    CanMove = GameBridge.CanMove(player),
                    InMeeting = inMeeting,
                });

                if (!isDead && track.Current.DeltaDistance >= teleportThreshold) bigMovers++;
            }

            // 两人及以上同时大位移 → 场景传送，整批开豁免窗口。
            // 单人瞬移（真正的作弊）不会命中这条 —— 那才是我们要抓的。
            if (bigMovers >= 2)
            {
                foreach (var t in Tracker.Tracks.Values) t.LastLegalTeleportTime = now;

                if (Config.VerboseLogging.Value)
                    Log?.LogInfo("[反作弊] 本帧 " + bigMovers + " 人同时大位移，判定为场景传送，整批豁免。");
            }

            // ══════════ 第二遍：逐人分析 ══════════
            foreach (var player in PlayerBuffer)
            {
                if (player == null) continue;

                var playerId = GameBridge.GetPlayerId(player);
                if (playerId < 0) continue;

                var isDead = GameBridge.IsDead(player);
                if (skipDead && isDead && skipThisTick) continue;

                // 鬼魂不参与移动类判定：死亡后穿墙、移动规则与活人完全不同，
                // 拿活人的阈值去套必然误报。
                if (isDead) continue;

                if (!Tracker.TryGet(playerId, out var track) || track == null) continue;

                // 速度上限必须按「这个玩家自己」算。
                // 用本机玩家的 TrueSpeed 套所有人是错的 —— 鬼魂和活人的理论速度不同，
                // 该做法以单一阈值套用全场，会把 3 倍速大厅中的正常玩家判为超速。
                var maxSpeed = fallbackMaxSpeed;
                var ownSpeed = GameBridge.GetMaxAllowedSpeed(player);
                if (ownSpeed > 0.01f) maxSpeed = ownSpeed * tolerance;

                EvidenceBuffer.Clear();

                Analyzer.AnalyzeMovement(track, now, maxSpeed, grace, EvidenceBuffer);

                if (inMeeting) Analyzer.AnalyzeMeetingMovement(track, now, EvidenceBuffer);

                foreach (var v in EvidenceBuffer) Verdicts.Submit(v, now);
            }
        }

        // ================= 生命周期边沿 =================

        private static void OnRoundEntered(float now)
        {
            _roundStartTime = now;
            _nextSampleTime = now;

            Tracker.Clear();
            Verdicts.ResetForNewRound();
            // 名单检查的两张表都按 PlayerId 记，每局 PlayerId 重新分配，必须清掉
            BanReported.Clear();
            BanChecked.Clear();
            // 拦截层的限频表按玩家 id 累积，新回合必须清掉，否则跨局会残留。
            Patches.RpcGuardPatch.ResetLogState();
            GameBridge.InvalidateLayerCache();

            Log?.LogInfo("[反作弊] 检测到进入对局，已重置检测状态。");

            // 记录本局玩家名单（受「记录谁进过房间」开关控制）
            try
            {
                var names = new List<string>();
                foreach (var p in GameBridge.GetPlayers())
                {
                    if (p == null) continue;
                    names.Add(GameBridge.GetPlayerName(p));
                }
                Core.HistoryLog.RecordRoundPlayers(names);
            }
            catch { }

            // 静态扫描放在进入对局时做一次：此时插件已全部加载完毕，
            // 且不会拖慢游戏启动。
            if (Config.EnableStaticScan.Value)
            {
                try
                {
                    ScanReport = Scanner.Scan();
                    Verdicts.SubmitAll(ScanReport.Violations, now);
                }
                catch (System.Exception ex)
                {
                    Log?.LogError($"[反作弊] 静态扫描异常：{ex}");
                }
            }
        }

        private static void OnRoundExited()
        {
            Log?.LogInfo("[反作弊] 已离开对局。");
            Tracker.Clear();
        }

        private static void OnMeetingStarted(float now)
        {
            // 会议会把所有人位置重置到会议桌，这属于合法传送
            foreach (var track in Tracker.Tracks.Values)
                track.LastLegalTeleportTime = now;

            Log?.LogInfo("[反作弊] 会议开始，已对所有玩家开启位置豁免窗口。");
        }

        private static void OnMeetingEnded(float now)
        {
            foreach (var track in Tracker.Tracks.Values)
            {
                track.LastLegalTeleportTime = now;
                track.LastMeetingMoveReportTime = float.NegativeInfinity;
            }

            Log?.LogInfo("[反作弊] 会议结束，已重置位置豁免窗口。");
        }

        // ================= 处置 =================

        private static void HandleKickRequested(PlayerVerdict verdict)
        {
            if (verdict == null) return;

            var clientId = GameBridge.GetClientIdByPlayerId(verdict.PlayerId);
            if (clientId < 0)
            {
                Log?.LogWarning($"[处置] 无法解析玩家「{verdict.Name}」的 ClientId，放弃踢出。");
                return;
            }

            if (GameBridge.KickPlayer(clientId, verdict.BanRequested))
                Log?.LogError($"[处置] 已{(verdict.BanRequested ? "封禁" : "踢出")}作弊玩家「{verdict.Name}」(PlayerId={verdict.PlayerId}, ClientId={clientId})。");
            else
                Log?.LogWarning($"[处置] 踢出「{verdict.Name}」失败——可能自己不是房主。");
        }
    }
}
