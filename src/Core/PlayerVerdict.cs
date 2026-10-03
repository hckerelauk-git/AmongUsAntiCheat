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
            return RiskLevel.Suspicious;
        }

        public void AddEvidence(Violation v)
        {
            if (v == null) return;
            if (Evidence.Count >= 128) Evidence.RemoveAt(0);
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
            BanRequested = false;
            LastNotifiedLevel = RiskLevel.Normal;
            AiConfirmed = false;
            AiSummary = null;
            AiConfidence = 0;
            Evidence.Clear();
        }
    }
}
