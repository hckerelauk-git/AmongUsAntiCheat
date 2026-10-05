using System.Collections.Generic;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 单个玩家的判定状态：命中的规则链。
    ///
    /// 从 VerdictEngine.cs 拆出来的原因：这个类**不依赖任何外部类型**
    /// （不需要 BepInEx、不需要游戏程序集），而 VerdictEngine 需要。
    /// 放在一起会导致「想单测 PlayerVerdict 就得先把 BepInEx 拖进来」，
    /// 拆开后它可以被测试工程直接编译进去。
    ///
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

        /// <summary>
        /// 同一条规则重复命中多少次后视为确定性证据。
        ///
        /// 为什么需要这条：像「持续超速」这种规则，单次命中的严重度是 High，
        /// 而等级判定只看 Critical —— 于是一个人连续超速 8 次、最高到 1.77 倍速，
        /// 却永远停在「可疑」，升不到高危。实测日志里就是这样。
        ///
        /// 这不是「分数累计」：分数累计是「不同规则的权重加起来够大就定罪」，
        /// 而这里是**同一条规则被独立确认了 N 次**，和超速判定内部
        /// 「连续 3 次采样才算一次命中」是同一个思路。
        /// </summary>
        public const int RepeatConfirmCount = 5;

        /// <summary>按规则类型累计的命中次数。</summary>
        private readonly Dictionary<ViolationKind, int> _kindCounts = new Dictionary<ViolationKind, int>();

        /// <summary>是否存在「同一条规则重复命中达到阈值」。</summary>
        public bool HasRepeatedRule
        {
            get
            {
                foreach (var c in _kindCounts.Values)
                    if (c >= RepeatConfirmCount) return true;
                return false;
            }
        }

        public bool Warned { get; set; }
        public bool KickIssued { get; set; }

        /// <summary>本次处置是否同时要求封禁（由「动手的方式 = 封禁」推导）。</summary>
        public bool BanRequested { get; set; }

        /// <summary>AI 二次研判是否已确认为作弊。</summary>
        public bool AiConfirmed { get; set; }

        /// <summary>最近一次 AI 分析的结论摘要（供界面展示）。</summary>
        public string AiSummary { get; set; }

        /// <summary>最近一次 AI 分析的置信度 0~1。</summary>
        public float AiConfidence { get; set; }

        /// <summary>
        /// 已经弹过通知的风险等级。
        ///
        /// 注意：**不能只用它来去重**。早期实现是「等级没升就不弹」，
        /// 结果一个玩家一旦升到高风险，之后所有作弊都被静默 ——
        /// 表现为「仅提示一次，后续作弊不再告警」。
        /// 现在它只负责判断「是否升级」，重复提示的节流交给
        /// <see cref="LastNotifiedEvidenceCount"/> 与 <see cref="LastNotifyTime"/>。
        /// </summary>
        public RiskLevel LastNotifiedLevel { get; set; } = RiskLevel.Normal;

        /// <summary>上次弹通知时的证据条数。证据增加说明又有新作弊，需要再提示。</summary>
        public int LastNotifiedEvidenceCount { get; set; }

        /// <summary>上次弹通知的时刻，用于重复提示的冷却。</summary>
        public float LastNotifyTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// 判断此刻是否应该为这个玩家弹通知。
        ///
        /// 抽成纯静态函数是为了能脱离 Unity 直接测 —— 通知频率这块出过
        /// 「只爆一次、后续作弊全静默」的线上问题，必须锁死。
        /// </summary>
        /// <param name="level">当前风险等级。</param>
        /// <param name="lastNotifiedLevel">上次弹通知时的等级。</param>
        /// <param name="evidenceCount">当前证据条数。</param>
        /// <param name="lastNotifiedEvidenceCount">上次弹通知时的证据条数。</param>
        /// <param name="now">当前时刻。</param>
        /// <param name="lastNotifyTime">上次弹通知的时刻。</param>
        /// <param name="repeatInterval">重复提示的最小间隔（秒）。</param>
        public static bool ShouldNotify(
            RiskLevel level,
            RiskLevel lastNotifiedLevel,
            int evidenceCount,
            int lastNotifiedEvidenceCount,
            float now,
            float lastNotifyTime,
            float repeatInterval)
        {
            // 正常状态不弹
            if (level <= RiskLevel.Normal) return false;

            // 等级升级：必弹。这是首次发现，或风险从可疑升到高。
            if (level > lastNotifiedLevel) return true;

            // 等级没变，但又有新证据，且冷却已过 → 再弹一次。
            // 没有这一条，玩家升到高风险之后的所有作弊都会被静默。
            return evidenceCount > lastNotifiedEvidenceCount &&
                   now - lastNotifyTime >= repeatInterval;
        }

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
        ///
        /// 不接收配置参数：判定只看「命中条数 / 是否有 Critical / AI 是否确认」，
        /// 与任何阈值都无关（阈值只影响证据的产生，不影响证据的汇总）。
        /// 之前保留了一个从未使用的 cfg 参数，容易让人误以为判定受配置影响，已移除。
        /// </summary>
        public RiskLevel EvaluateLevel()
        {
            if (Evidence.Count == 0) return RiskLevel.Normal;
            if (AiConfirmed) return RiskLevel.Confirmed;
            if (CriticalCount > 0) return RiskLevel.HighRisk;
            // 同一规则被重复确认多次 —— 偶发解释不了，按高危处理
            if (HasRepeatedRule) return RiskLevel.HighRisk;
            return RiskLevel.Suspicious;
        }

        public void AddEvidence(Violation v)
        {
            if (v == null) return;

            if (Evidence.Count >= 128)
            {
                // 淘汰最旧一条时，计数也要跟着减，否则 _kindCounts 会越算越多
                var dropped = Evidence[0];
                Evidence.RemoveAt(0);
                if (_kindCounts.TryGetValue(dropped.Kind, out var d) && d > 0)
                    _kindCounts[dropped.Kind] = d - 1;
            }

            Evidence.Add(v);
            EvidenceCount++;
            if (v.Severity == Severity.Critical) CriticalCount++;

            _kindCounts.TryGetValue(v.Kind, out var c);
            _kindCounts[v.Kind] = c + 1;
        }

        public void Reset()
        {
            EvidenceCount = 0;
            CriticalCount = 0;
            Warned = false;
            KickIssued = false;
            BanRequested = false;
            LastNotifiedLevel = RiskLevel.Normal;
            LastNotifiedEvidenceCount = 0;
            LastNotifyTime = float.NegativeInfinity;
            AiConfirmed = false;
            AiSummary = null;
            AiConfidence = 0;
            Evidence.Clear();
            _kindCounts.Clear();
        }
    }
}
