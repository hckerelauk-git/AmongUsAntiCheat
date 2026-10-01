using System.Collections.Generic;
using AmongUsAntiCheat.Config;
using AmongUsAntiCheat.Core;
using BepInEx.Logging;
using UnityEngine;

namespace AmongUsAntiCheat
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

        /// <summary>RPC 事件采集器（用于 AI 分析与事后追溯）。</summary>
        public static Core.RpcEventRecorder Recorder { get; private set; }

        /// <summary>AI 分析编排器（异步调大模型二次研判）。</summary>
        public static Core.RpcAiAnalyzer AiAnalyzer { get; private set; }

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
        private static bool _wasInMeeting;

        /// <summary>
        /// 风险等级巡检间隔（秒）。
        /// 每帧遍历全部玩家判定并调用 EvaluateLevel 是不必要的开销，
        /// 通知本身又是给人看的，5Hz 足够。
        /// </summary>
        private const float RiskCheckInterval = 0.2f;
        private static float _nextRiskCheckTime;

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
            AiAnalyzer = new Core.RpcAiAnalyzer(config, Recorder);
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

            // 只弹第一条，避免用户一次改一堆时刷屏
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
        }

        /// <summary>每帧调用：整个检测循环的驱动入口。</summary>
        // ================= AI 分析驱动 =================

        /// <summary>
        /// 把异步 AI 产物从内部队列回收到 VerdictEngine（主线程调用）。
        /// </summary>
        private static void DrainAiResults()
        {
            AiAnalyzer?.DrainPending(Verdicts);
        }

        /// <summary>
        /// 检查每个玩家的判定等级是否**升级**，升级才弹右下角通知。
        ///
        /// 关键点：只在等级变化时提示一次。
        /// 如果改成每帧提示，一个持续作弊的玩家会把屏幕刷满。
        /// </summary>
        private static void CheckRiskLevelChanges()
        {
            var cfg = Config;
            var verdicts = Verdicts;
            if (cfg == null || verdicts == null) return;

            foreach (var v in verdicts.Verdicts.Values)
            {
                var level = v.EvaluateLevel(cfg);

                if (level <= RiskLevel.Normal)
                {
                    // 回到正常：重置标记，允许下次重新提示
                    v.LastNotifiedLevel = RiskLevel.Normal;
                    continue;
                }

                // 未升级 → 不重复弹
                if (level <= v.LastNotifiedLevel) continue;

                v.LastNotifiedLevel = level;

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
            if (!IsReady) return;

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

            if (!inGame)
            {
                // 不在对局中也要驱动，保持调用方契约
                Verdicts.Tick(now, deltaTime);
                DrainAiResults();
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

            // 衰减
            Verdicts.Tick(now, deltaTime);

            // 把异步 AI 产物回收到 VerdictEngine（主线程）
            DrainAiResults();

            // 判定等级升级 → 右下角弹通知（节流到 5Hz，避免每帧全量遍历）
            if (now >= _nextRiskCheckTime)
            {
                _nextRiskCheckTime = now + RiskCheckInterval;
                CheckRiskLevelChanges();
            }
        }

        // ================= 采样 =================

        private static void SampleAllPlayers(float now, bool inMeeting)
        {
            // 复用缓冲区：热路径不分配新列表
            GameBridge.GetPlayersInto(PlayerBuffer);
            if (PlayerBuffer.Count == 0) return;

            _sampleTick++;

            var maxSpeed = GameBridge.GetMaxAllowedSpeed() * Config.MaxSpeedTolerance.Value;
            var grace = Config.RoundStartGracePeriod.Value;

            // 死亡玩家降频：死人不会再瞬移/超速，没必要每轮都做全套采样。
            // 这是移植自 Amethyst 的「不更新死亡玩家 + 跳帧」策略。
            var skipDead = Config.PerfDontUpdateDead.Value;
            var deadEvery = System.Math.Max(1, Config.PerfDeadSkipFrames.Value);
            var skipThisTick = deadEvery > 1 && (_sampleTick % deadEvery) != 0;

            foreach (var player in PlayerBuffer)
            {
                if (player == null) continue;

                var playerId = GameBridge.GetPlayerId(player);
                if (playerId < 0) continue;

                var isDead = GameBridge.IsDead(player);
                if (skipDead && isDead && skipThisTick) continue;

                var name = GameBridge.GetPlayerName(player);
                var track = Tracker.GetOrCreate(playerId, name, now);

                // 角色能力：用 Role.CanVent 而不是阵营，避免把 Viper 这类
                // 「船员阵营但能钻管道」的角色误判成作弊。
                var roleKnown = GameBridge.TryGetCanVent(player, out var canVent);

                var snapshot = new PlayerSnapshot
                {
                    Time = now,
                    Position = GameBridge.GetPosition(player),
                    IsDead = isDead,
                    InVent = GameBridge.IsInVent(player),
                    IsImpostor = GameBridge.IsImpostor(player),
                    CanVent = canVent,
                    RoleKnown = roleKnown,
                    CanMove = GameBridge.CanMove(player),
                    InMeeting = inMeeting,
                };

                track.Push(snapshot);

                EvidenceBuffer.Clear();

                Analyzer.AnalyzeMovement(track, now, maxSpeed, grace, EvidenceBuffer);

                if (inMeeting) Analyzer.AnalyzeMeetingMovement(track, now, EvidenceBuffer);

                foreach (var v in EvidenceBuffer) Verdicts.Submit(v, now);

                // 提交证据后让 AI 分析器评估是否触发异步分析。
                // 必须放在 Submit 之后——否则 AiAnalyzer 看到的命中条数还没到门槛。
                AiAnalyzer?.MaybeAnalyze(Verdicts.TryGet(playerId, out var vd) ? vd : null);
            }
        }

        // ================= 生命周期边沿 =================

        private static void OnRoundEntered(float now)
        {
            _roundStartTime = now;
            _nextSampleTime = now;

            Tracker.Clear();
            Verdicts.ResetForNewRound();
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
