using System;
using System.Collections.Generic;
using BepInEx.Logging;
using ApexCheatEnder.Config;
using UnityEngine;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 判定层：把命中的规则汇总成「这个玩家是否作弊」的结论，并驱动响应动作。
    ///
    /// 模型是「规则命中」，不计分：
    /// 每条规则命中即记一笔，命中确定性规则立即定为高危，
    /// 不设累加与衰减 —— 确定性证据不应随时间衰减。
    ///
    /// 注意：PlayerVerdict 已拆到同命名空间的 PlayerVerdict.cs。
    /// 拆分的理由是它本身不依赖任何外部类型，而本类需要 BepInEx ——
    /// 放一起会让「想单测 PlayerVerdict」变成必须先引入 BepInEx。
    /// </summary>
    public sealed class VerdictEngine
    {
        /// <summary>
        /// 按玩家号取身份串（好友码 / 平台 ID）。
        /// 核心层不引用 GameBridge，由运行时注入；未注入时返回空串。
        /// </summary>
        public static System.Func<int, string> DescribeIdentity;

        private static string IdentityOf(int playerId)
        {
            try { return DescribeIdentity?.Invoke(playerId) ?? string.Empty; }
            catch { return string.Empty; }
        }

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
        internal LocalEventHistory History { get; } = new LocalEventHistory();

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

            History.Add(violation);
            if (violation.PlayerId < 0)
            {
                if (GlobalEvidence.Count >= LocalEventHistory.Capacity) GlobalEvidence.RemoveAt(0);
                GlobalEvidence.Add(violation);
                _log.LogWarning($"[全局证据] {violation}");
                return;
            }

            var verdict = GetOrCreate(violation.PlayerId, violation.PlayerName);
            verdict.AddEvidence(violation);

            // 身份（好友码 / 平台 ID）：**命中了却拿不到可封禁的标识，等于白抓。**
            // 现场就是这样：日志里只有「777(6)」，玩家号每局重分配，事后根本对不上人。
            var identity = IdentityOf(verdict.PlayerId);

            _log.LogWarning(
                $"[规则命中] {verdict.Name}({verdict.PlayerId}){identity} 命中 {violation.Kind}，本局累计 {verdict.EvidenceCount} 条 | {violation.Detail}");

            // 落盘留证（受「记录作弊判定」开关控制）
            HistoryLog.RecordViolation(verdict.Name, verdict.PlayerId,
                violation.Kind.ToString(), violation.Detail, identity);

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
            var level = verdict.EvaluateLevel();
            if (!verdict.Warned)
            {
                verdict.Warned = true;
                _log.LogWarning($"[命中] 玩家「{verdict.Name}」命中 {verdict.EvidenceCount} 条规则，当前等级：{level}。");
                PlayerWarned?.Invoke(verdict);
            }

            if (!verdict.KickIssued && level >= RiskLevel.HighRisk && _cfg.AllowAutoKick.Value)
            {
                // 自动处置也要尊重「动手的方式」：
                //   忽略 / 警告 → 即使开了自动踢人也不动手
                //   踢出        → 踢出
                //   封禁        → 踢出并附带封禁
                var mode = _cfg.DispositionMode?.Value ?? DispositionModes.Warn;
                if (!DispositionModes.CanKick(mode))
                {
                    if (!verdict.Warned) { /* 已在上面处理过警告，这里只是不踢 */ }
                    return;
                }

                verdict.KickIssued = true;
                verdict.BanRequested = DispositionModes.ShouldBan(mode);

                _log.LogError($"[处置] 玩家「{verdict.Name}」命中确定性规则，执行处置（{mode}）。");
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
