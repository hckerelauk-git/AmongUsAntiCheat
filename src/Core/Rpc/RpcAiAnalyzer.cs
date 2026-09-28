using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AmongUsAntiCheat.Config;
using UnityEngine;

namespace AmongUsAntiCheat.Core
{
    /// <summary>
    /// AI 分析编排器。
    ///
    /// 工作流：
    ///   1. 玩家命中的规则条数 ≥ <see cref="AntiCheatConfig.AiMinHitsToTrigger"/>
    ///      且不在 <see cref="AntiCheatConfig.AiCooldownSeconds"/> 冷却内
    ///      → 触发异步分析
    ///   2. <see cref="RpcFeatureExtractor"/> 把玩家环形缓冲的 RPC 序列 + 上下文打包成 JSON
    ///   3. <see cref="RpcAiClient"/> 发 HTTP 请求
    ///   4. AI 返回的结构化判定经解析后作为 <see cref="Violation"/> 投递回 <see cref="VerdictEngine"/>
    ///
    /// 所有异步产物通过 <see cref="DrainPending"/> 在主线程帧循环里取出，
    /// 不会破坏 Unity 的「主线程唯一可访问 Scene」规则。
    /// </summary>
    public sealed class RpcAiAnalyzer
    {
        /// <summary>单次 AI 分析的运行状态（供界面展示进度）。</summary>
        public sealed class AiAnalysisState
        {
            /// <summary>是否正在分析中。</summary>
            public bool Running;

            /// <summary>展示文案：分析中 / 结论摘要 / 失败原因。</summary>
            public string Summary;

            /// <summary>置信度 0~1（仅成功时有效）。</summary>
            public float Confidence;

            /// <summary>本次分析覆盖的 RPC 条数。</summary>
            public int EventCount;
        }

        private readonly AntiCheatConfig _cfg;
        private readonly RpcEventRecorder _recorder;

        private readonly ConcurrentDictionary<int, float> _lastAnalysisTime
            = new ConcurrentDictionary<int, float>();
        private readonly ConcurrentQueue<Violation> _pendingViolations
            = new ConcurrentQueue<Violation>();

        /// <summary>每个玩家最近一次分析的状态，供设置界面展示进度。</summary>
        private readonly ConcurrentDictionary<int, AiAnalysisState> _states
            = new ConcurrentDictionary<int, AiAnalysisState>();

        /// <summary>当前生效的供应商对象（由配置里的名字解析得到）。</summary>
        public AiProvider Provider => AiProviders.Resolve(_cfg?.AiProvider?.Value);

        /// <summary>配置是否完整（开关打开 + 密钥已填）。</summary>
        public bool IsConfigured
        {
            get
            {
                if (_cfg == null) return false;
                if (!_cfg.AiAnalysisEnabled.Value) return false;
                return Provider.IsUsableWith(_cfg.AiApiKey.Value);
            }
        }

        /// <summary>
        /// 配置缺失时给人看的原因。
        ///
        /// 措辞是写给普通玩家看的：说「你还没填密钥」而不是
        /// 「endpoint 为空」，后者会让人以为是插件坏了。
        /// </summary>
        public string ConfigProblem
        {
            get
            {
                if (_cfg == null) return "插件还没初始化完";
                if (!_cfg.AiAnalysisEnabled.Value) return "AI 分析没开";
                if (Provider.RequiresApiKey &&
                    string.IsNullOrEmpty(Provider.NormalizeKey(_cfg.AiApiKey.Value)))
                    return "还没填 " + Provider.DisplayName + " 的密钥";
                return null;
            }
        }

        /// <summary>取某玩家的分析状态；从未分析过返回 null。</summary>
        public AiAnalysisState GetState(int playerId) =>
            _states.TryGetValue(playerId, out var st) ? st : null;

        /// <summary>
        /// 系统提示词。说明 AI 的角色、输入结构与输出格式。
        /// 把它写在外置字符串里方便后续调整，不必改代码。
        /// </summary>
        private const string SystemPrompt = @"
你是 Among Us 反作弊专家。玩家会给你一个玩家的最近 RPC 操作序列 + 游戏状态，
你需要判断这个玩家是否在作弊。

【输入】JSON 对象，包含：
- player: 玩家 ID、角色、是否存活、当前位置
- game: 游戏阶段、已过秒数、玩家总数
- events: 按时间排序的 RPC 事件数组，每条含 rpc 名 + 参数 + 距离 + 冷却剩余

【判断重点】
1. 船员（Crewmate / Engineer / Scientist / Tracker / Detective / Noisemaker）发起击杀
2. 内鬼之间的击杀
3. 击杀间隔 < 角色冷却时间（基础 25s）
4. 会议中 / 死后 / 任务阶段发起击杀
5. 位置超出房间物理可达范围
6. 短时间内多次 RpcSnapTo（瞬移作弊特征）

【输出】必须是合法 JSON，不要任何额外说明：
{
  ""suspicious"": true/false,
  ""confidence"": 0.0~1.0,
  ""reason"": ""一句话给玩家看的理由"",
  ""violations"": [{ ""kind"": ""KillCooldownBypass"", ""severity"": ""High"" }]
}
";
        public RpcAiAnalyzer(AntiCheatConfig cfg, RpcEventRecorder recorder)
        {
            _cfg = cfg;
            _recorder = recorder;
        }

        /// <summary>在主线程调用，决定是否触发一次异步分析（自动触发）。</summary>
        public void MaybeAnalyze(PlayerVerdict verdict)
        {
            if (!IsConfigured) return;
            if (verdict == null) return;

            // 自动触发的门槛：命中规则条数达到配置要求。
            // 一条都没命中的正常玩家不送 AI，避免无意义调用与费用浪费。
            var minHits = _cfg.AiMinHitsToTrigger?.Value ?? 1;
            if (verdict.EvidenceCount < minHits) return;

            // 已确认过的不重复分析
            if (verdict.AiConfirmed) return;

            var now = Time.time;
            if (_lastAnalysisTime.TryGetValue(verdict.PlayerId, out var last) &&
                now - last < _cfg.AiCooldownSeconds.Value) return;
            _lastAnalysisTime[verdict.PlayerId] = now;

            var events = _recorder.Drain(verdict.PlayerId);
            if (events.Length == 0) return;

            StartAnalysis(verdict, events);
        }

        /// <summary>
        /// 手动触发：管理员针对指定玩家强制发起一次分析，
        /// 跳过命中条数门槛与冷却（但仍要求配置完整）。
        /// </summary>
        /// <returns>是否成功发起。</returns>
        public bool ManualAnalyze(int playerId, PlayerVerdict verdict)
        {
            if (!IsConfigured) return false;
            if (verdict == null) return false;

            // 手动分析用 Snapshot 而不是 Drain——保留事件供后续查看
            var events = _recorder.Snapshot(playerId);
            if (events.Length == 0) return false;

            _lastAnalysisTime[verdict.PlayerId] = Time.time;
            StartAnalysis(verdict, events);
            return true;
        }

        private void StartAnalysis(PlayerVerdict verdict, RpcEvent[] events)
        {
            var state = new AiAnalysisState
            {
                Running = true,
                Summary = "分析中...",
                EventCount = events.Length,
            };
            _states[verdict.PlayerId] = state;

            var snapshot = CaptureSnapshot(verdict, events);

            // 异步分析，后台线程跑；结果通过 _states 与 _pendingViolations 回主线程
            _ = Task.Run(() => RunAnalysisAsync(verdict, snapshot, state));
        }

        /// <summary>主线程每帧调用，把 AI 的产物取出送到 VerdictEngine。</summary>
        public void DrainPending(VerdictEngine verdicts)
        {
            if (verdicts == null) return;
            while (_pendingViolations.TryDequeue(out var v))
                verdicts.Submit(v, Time.time);
        }

        private static Dictionary<string, object> CaptureSnapshot(PlayerVerdict verdict, RpcEvent[] events)
        {
            var player = GameBridge.GetPlayerById(verdict.PlayerId);
            var pos = GameBridge.GetPosition(player);
            var alive = !GameBridge.IsDead(player);

            return new Dictionary<string, object>
            {
                ["player"] = new
                {
                    id = verdict.PlayerId,
                    name = AntiCheatRuntime.Config != null &&
                           !AntiCheatRuntime.Config.AiSendPlayerNames.Value
                        ? $"Player#{verdict.PlayerId}"
                        : verdict.Name,
                    role = GameBridge.IsImpostor(player) ? "Impostor" : "Crewmate",
                    alive,
                    position = new[] { pos.X, pos.Y },
                },
                ["game"] = new
                {
                    phase = GameBridge.IsInMeeting ? "Meeting" : "Task",
                    elapsed = Time.time,
                    playerCount = GameBridge.GetPlayers().Count,
                },
                ["events"] = events,
            };
        }

        private async Task RunAnalysisAsync(
            PlayerVerdict verdict, Dictionary<string, object> snapshot, AiAnalysisState state)
        {
            AiVerdict aiVerdict;
            try
            {
                // 每次现取供应商和密钥，而不是缓存。
                // 这样用户在设置界面换了供应商或改了密钥，下一次分析立刻用新的，
                // 不用重启游戏。
                var provider = Provider;
                var key = provider.NormalizeKey(_cfg.AiApiKey.Value);
                var timeout = _cfg.AiTimeoutSeconds.Value;

                var client = new RpcAiClient(
                    provider.Endpoint,
                    key,
                    provider.Model,
                    timeout);

                var userPrompt = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });

                aiVerdict = await client.AnalyzeAsync(SystemPrompt, userPrompt,
                    new System.Threading.CancellationTokenSource(
                        TimeSpan.FromSeconds(timeout + 2)).Token);
            }
            catch (Exception ex)
            {
                // 失败静默：AI 是辅助，规则引擎继续工作。
                // 但要把失败原因记进 state，界面上要能看见。
                AntiCheatRuntime.Log?.LogInfo($"[AI] {verdict.Name} 分析失败：{ex.Message}");
                if (state != null)
                {
                    state.Running = false;
                    state.Summary = "分析失败：" + ex.Message;
                }
                return;
            }

            // 未判定为可疑：也要回写 state，避免界面一直卡在「分析中」
            if (aiVerdict == null || aiVerdict.IsEmpty || !aiVerdict.Suspicious)
            {
                if (state != null)
                {
                    state.Running = false;
                    state.Summary = aiVerdict == null || aiVerdict.IsEmpty
                        ? "无结果"
                        : "AI 判定：正常";
                    state.Confidence = aiVerdict?.Confidence ?? 0f;
                }
                return;
            }

            // 映射 AI 的 ViolationKind 到本地枚举，失败归类为 IllegalAction
            var localKind = MapViolationKind(aiVerdict.ViolationKind);
            var localSeverity = aiVerdict.Confidence >= 0.7f ? Severity.High
                             : aiVerdict.Confidence >= 0.4f ? Severity.Medium
                             : Severity.Low;

            var violation = new Violation(
                localKind,
                localSeverity,
                verdict.PlayerId,
                verdict.Name,
                Time.time,
                $"AI 分析（置信度 {(aiVerdict.Confidence * 100f):F0}%）：{aiVerdict.Reason}",
                new Dictionary<string, float>
                {
                    ["ai_confidence"] = aiVerdict.Confidence,
                    ["ai_events_analyzed"] = snapshot.TryGetValue("events", out var ev) && ev is RpcEvent[] arr ? arr.Length : 0f,
                });

            _pendingViolations.Enqueue(violation);

            // 回写玩家判定：AI 确认后该玩家升级为 RiskLevel.Confirmed
            verdict.AiConfirmed = true;
            verdict.AiSummary = aiVerdict.Reason;
            verdict.AiConfidence = aiVerdict.Confidence;

            if (state != null)
            {
                state.Running = false;
                state.Summary = aiVerdict.Reason;
                state.Confidence = aiVerdict.Confidence;
            }
        }

        private static ViolationKind MapViolationKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return ViolationKind.InvalidRpc;
            return kind.ToLowerInvariant() switch
            {
                "killcooldownbypass" or "cooldownbypass" => ViolationKind.KillCooldownBypass,
                "killtoofar"                              => ViolationKind.KillTooFar,
                "killwhilenotimpostor" or "illegalkill"    => ViolationKind.KillWhileNotImpostor,
                "ghostaction" or "ghost"                   => ViolationKind.GhostAction,
                "illegalvent" or "vent"                    => ViolationKind.IllegalVent,
                "tasktoofast" or "task"                    => ViolationKind.TaskTooFast,
                "remotetask"                              => ViolationKind.RemoteTask,
                "teleport" or "snapto"                     => ViolationKind.Teleport,
                "speedhack" or "speed"                     => ViolationKind.SpeedHack,
                _                                          => ViolationKind.InvalidRpc,
            };
        }
    }
}