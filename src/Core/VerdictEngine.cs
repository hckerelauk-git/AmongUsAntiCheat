using System;
using System.Collections.Generic;
using BepInEx.Logging;
using AmongUsAntiCheat.Config;
using UnityEngine;

namespace AmongUsAntiCheat.Core
{
    /// <summary>
    /// 单个玩家的判定状态：命中的规则链。
    /// 规则链必须保留，否则玩家申诉时无法说明「为什么判我」。
    /// </summary>
    public sealed class PlayerVerdict
    {
        public int PlayerId { get; set; }
        public string Name { get; set; } = "?";

        /// <summary>本局命中的规则条数。</summary>
        public int EvidenceCount { get; set; }

        /// <summary>其中确定性证据（Critical）的条数。</summary>
        public int CriticalCount { get; set; }

        public bool Warned { get; set; }
        public bool KickIssued { get; set; }

        /// <summary>AI 二次研判是否已确认为作弊。</summary>
        public bool AiConfirmed { get; set; }

        /// <summary>最近一次 AI 分析的结论摘要（供界面展示）。</summary>
        public string AiSummary { get; set; }

        /// <summary>最近一次 AI 分析的置信度 0~1。</summary>
        public float AiConfidence { get; set; }

        /// <summary>
        /// 已经弹过通知的风险等级。
        /// 用于「只在等级升级时提示一次」，避免同一玩家每帧刷屏。
        /// </summary>
        public RiskLevel LastNotifiedLevel { get; set; } = RiskLevel.Normal;

        public List<Violation> Evidence { get; } = new List<Violation>(16);

        /// <summary>
        /// 综合判定该玩家的风险等级。
        ///
        /// 这是界面打标记的**唯一依据**——不允许在别处拿分数做判断。
        /// 模型是「规则命中」，没有分数累积：
        ///   1. 毫无命中        → 正常（不能因为"被追踪过"就标记）
        ///   2. AI 已确认       → 已确认
        ///   3. 有确定性命中    → 高危（物理上不可能的行为）
        ///   4. 其余命中        → 已命中（记录在案，等你处置）
        /// </summary>
        public RiskLevel EvaluateLevel(AntiCheatConfig cfg)
        {
            if (Evidence.Count == 0) return RiskLevel.Normal;
            if (AiConfirmed) return RiskLevel.Confirmed;
            if (CriticalCount > 0) return RiskLevel.HighRisk;
            return RiskLevel.Suspicious;
        }

        public void AddEvidence(Violation v)
        {
            Evidence.Add(v);
            EvidenceCount++;
            if (v.Severity == Severity.Critical) CriticalCount++;
        }

        public void Reset()
        {
            EvidenceCount = 0;
            CriticalCount = 0;
            Warned = false;
            KickIssued = false;
            Evidence.Clear();
        }
    }

    /// <summary>
    /// 判定层：把命中的规则汇总成「这个玩家是否作弊」的结论，并驱动响应动作。
    ///
    /// 模型是「规则命中」，不计分：
    /// 每条规则命中即记一笔，命中确定性规则立即定为高危，
    /// 不存在累加与衰减——确定性的东西不该被时间洗掉。
    /// </summary>
    public sealed class VerdictEngine
    {
        private readonly AntiCheatConfig _cfg;
        private readonly ManualLogSource _log;
        private readonly Dictionary<int, PlayerVerdict> _verdicts = new Dictionary<int, PlayerVerdict>(16);

        /// <summary>全局性证据（静态扫描命中的作弊插件等），不归属具体玩家。</summary>
        public List<Violation> GlobalEvidence { get; } = new List<Violation>();

        /// <summary>请求踢出某个玩家。由 Unity 侧订阅并调用游戏 API。</summary>
        public event Action<PlayerVerdict> KickRequested;

        /// <summary>玩家被警告。由 UI 侧订阅做高亮。</summary>
        public event Action<PlayerVerdict> PlayerWarned;

        public IReadOnlyDictionary<int, PlayerVerdict> Verdicts => _verdicts;

        public VerdictEngine(AntiCheatConfig cfg, ManualLogSource log)
        {
            _cfg = cfg;
            _log = log;
        }

        public PlayerVerdict GetOrCreate(int playerId, string name)
        {
            if (_verdicts.TryGetValue(playerId, out var v))
            {
                if (!string.IsNullOrEmpty(name)) v.Name = name;
                return v;
            }
            var created = new PlayerVerdict { PlayerId = playerId, Name = name ?? "?" };
            _verdicts[playerId] = created;
            return created;
        }

        public bool TryGet(int playerId, out PlayerVerdict verdict) => _verdicts.TryGetValue(playerId, out verdict);

        /// <summary>提交一条证据。全局证据走单独通道。</summary>
        public void Submit(Violation violation, float now)
        {
            if (violation == null) return;

            // 同一玩家同一条规则在 0.25 秒内只记一次，避免重复 Hook/RPC 把命中条数瞬间刷满。
            var existing = violation.PlayerId >= 0 ? GetOrCreate(violation.PlayerId, violation.PlayerName) : null;
            if (existing != null && existing.Evidence.Count > 0)
            {
                var last = existing.Evidence[existing.Evidence.Count - 1];
                if (last.Kind == violation.Kind && Math.Abs(now - last.Timestamp) < 0.25f) return;
            }

            if (violation.PlayerId < 0)
            {
                GlobalEvidence.Add(violation);
                _log.LogWarning($"[全局证据] {violation}");
                return;
            }

            var verdict = GetOrCreate(violation.PlayerId, violation.PlayerName);
            verdict.AddEvidence(violation);

            _log.LogWarning(
                $"[规则命中] {verdict.Name}({verdict.PlayerId}) 命中 {violation.Kind}，本局累计 {verdict.EvidenceCount} 条 | {violation.Detail}");

            Evaluate(verdict, now);
        }

        /// <summary>批量提交。</summary>
        public void SubmitAll(IEnumerable<Violation> violations, float now)
        {
            if (violations == null) return;
            foreach (var v in violations) Submit(v, now);
        }

        /// <summary>按命中等级决定是否通知 / 处置。</summary>
        private void Evaluate(PlayerVerdict verdict, float now)
        {
            var level = verdict.EvaluateLevel(_cfg);
            if (!verdict.Warned)
            {
                verdict.Warned = true;
                _log.LogWarning($"[命中] 玩家「{verdict.Name}」命中 {verdict.EvidenceCount} 条规则，当前等级：{level}。");
                PlayerWarned?.Invoke(verdict);
            }

            if (!verdict.KickIssued && level >= RiskLevel.HighRisk && _cfg.AllowAutoKick.Value)
            {
                verdict.KickIssued = true;
                _log.LogError($"[处置] 玩家「{verdict.Name}」命中确定性规则，执行处置。");
                KickRequested?.Invoke(verdict);
            }
        }

        /// <summary>每帧调用。规则命中模型下无需衰减，保留空实现以维持调用方契约。</summary>
        public void Tick(float now, float deltaTime) { }

        /// <summary>新回合开始：清空本回合的规则命中，保留全局证据。</summary>
        public void ResetForNewRound()
        {
            foreach (var v in _verdicts.Values) v.Reset();
            _log.LogInfo("[判定] 新回合开始，本局规则命中已清空。");
        }

        public void Forget(int playerId) => _verdicts.Remove(playerId);

        public void Clear()
        {
            _verdicts.Clear();
            GlobalEvidence.Clear();
        }

        /// <summary>按命中条数降序返回当前所有玩家判定，供 UI 展示。</summary>
        public List<PlayerVerdict> RankedVerdicts()
        {
            var list = new List<PlayerVerdict>(_verdicts.Values);
            list.Sort((a, b) => b.EvidenceCount.CompareTo(a.EvidenceCount));
            return list;
        }
    }
}
