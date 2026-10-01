using System;
using System.Collections.Generic;

namespace AmongUsAntiCheat.Core
{
    /// <summary>
    /// 违规类型。命名上刻意区分「静态检测」（作弊软件存在证据）
    /// 与「行为检测」（运行时异常行为），因为两者置信度语义不同：
    /// 静态证据几乎不会误报，行为证据必须靠时间窗口累计。
    /// </summary>
    public enum ViolationKind
    {
        // ---------- 静态层：作弊软件存在证据 ----------
        /// <summary>命中了已知作弊插件的特征（GUID / 名称 / 程序集）。</summary>
        KnownCheatPlugin,
        /// <summary>加载了不在白名单内的可疑 BepInEx 插件。</summary>
        UnknownPlugin,
        /// <summary>检测到内存注入 / 反编译特征（如 Il2Cpp 方法被 detour）。</summary>
        MemoryTamper,

        // ---------- 行为层：运动学异常 ----------
        /// <summary>单位时间位移超过理论上限，且无合法传送来源。</summary>
        Teleport,
        /// <summary>持续超速（速度修改）。</summary>
        SpeedHack,
        /// <summary>运动轨迹穿过墙体碰撞体。</summary>
        WallClip,

        // ---------- 事件层：关键动作参数非法 ----------
        /// <summary>击杀距离超过当前设置允许的最大距离。</summary>
        KillTooFar,
        /// <summary>击杀间隔短于角色冷却时间。</summary>
        KillCooldownBypass,
        /// <summary>非内鬼角色执行了击杀。</summary>
        KillWhileNotImpostor,
        /// <summary>任务完成间隔短于物理上可能的最短时间。</summary>
        TaskTooFast,
        /// <summary>在离任务点很远的位置提交任务（远程做任务）。</summary>
        RemoteTask,
        /// <summary>非法使用通风管（非内鬼 / 距离过远）。</summary>
        IllegalVent,
        /// <summary>已死亡玩家仍执行了活人动作。</summary>
        GhostAction,

        // ---------- 会议层 ----------
        /// <summary>会议进行中玩家位置发生了位移。</summary>
        MoveDuringMeeting,
        /// <summary>会议期间的投票/报告行为异常。</summary>
        IllegalMeetingAction,
        /// <summary>开局保护期内发起会议 / 报告尸体（刷屏或破坏开局）。</summary>
        EarlyMeeting,

        // ---------- 破坏层 ----------
        /// <summary>非内鬼阵营的玩家触发了破坏系统。</summary>
        SabotageWhileNotImpostor,
        /// <summary>会议期间触发破坏系统。</summary>
        SabotageDuringMeeting,
        /// <summary>向不存在的破坏目标 / 越界的系统编号发起破坏。</summary>
        InvalidSabotageTarget,

        // ---------- 通讯层 ----------
        /// <summary>聊天消息频率异常（刷屏）。</summary>
        ChatFlood,
        /// <summary>聊天消息内容非法（空消息、超长、含控制字符）。</summary>
        IllegalChat,
        /// <summary>昵称非法（空、超长、含控制字符或标签）。</summary>
        IllegalName,

        // ---------- 角色动作层 ----------
        /// <summary>不具备变形能力的角色执行了变形。</summary>
        IllegalShapeshift,
        /// <summary>不具备保护能力的角色执行了保护。</summary>
        IllegalProtect,

        // ---------- 通风管 / 滑索进阶 ----------
        /// <summary>使用了不存在的通风管编号（伪造管道）。</summary>
        VentForgedId,
        /// <summary>会议期间使用通风管。</summary>
        VentDuringMeeting,
        /// <summary>非房主强制把他人踢出通风管。</summary>
        VentForceOther,
        /// <summary>滑索使用异常（非法时机或越界）。</summary>
        ZiplineAbuse,

        // ---------- 网络层 ----------
        /// <summary>收到参数非法的 RPC 调用。</summary>
        InvalidRpc,
        /// <summary>客户端上报状态与服务端权威状态长期不一致。</summary>
        StateDesync,
        /// <summary>收到异常大的数据包（可能意图拖垮房间）。</summary>
        OversizedPacket,
    }

    /// <summary>
    /// 风险判定等级。
    ///
    /// 与 <see cref="Severity"/> 的区别：
    /// Severity 描述**单条命中**的确信程度，是输入；
    /// RiskLevel 是对某个玩家**综合判定后的结论**，是输出。
    ///
    /// 界面上打标记必须依据本枚举——
    /// 否则只要玩家被追踪过（哪怕零命中）也会被标记，属于无依据标记。
    /// </summary>
    public enum RiskLevel
    {
        /// <summary>正常：一条规则都没命中。</summary>
        Normal = 0,

        /// <summary>已命中：命中了规则，但都不是确定性的，需要你确认。</summary>
        Suspicious = 1,

        /// <summary>高危：命中了确定性（Critical）规则，物理上不可能发生。</summary>
        HighRisk = 2,

        /// <summary>已确认：AI 二次研判确认为作弊。</summary>
        Confirmed = 3,
    }

    /// <summary>命中严重度。决定这条命中算确定性还是参考性。</summary>
    public enum Severity
    {
        /// <summary>提示级。单独出现不构成判定，仅累积。</summary>
        Low = 0,
        /// <summary>可疑级。可能是网络抖动或误报。</summary>
        Medium = 1,
        /// <summary>高度可疑。需要多次确认或配合其他证据。</summary>
        High = 2,
        /// <summary>确定级。物理上不可能发生，可直接处置。</summary>
        Critical = 3,
    }

    /// <summary>
    /// 一条违规证据。这是整个反作弊系统内部流通的统一货币：
    /// 检测层负责生产，判定层负责消费。
    /// </summary>
    public sealed class Violation
    {
        public ViolationKind Kind { get; }
        public Severity Severity { get; }

        /// <summary>涉事玩家的 PlayerId；-1 表示全局性证据（如检测到作弊插件）。</summary>
        public int PlayerId { get; }

        /// <summary>涉事玩家名，仅用于日志可读性。</summary>
        public string PlayerName { get; }

        /// <summary>发生时刻（游戏内时间 Time.time）。</summary>
        public float Timestamp { get; }

        /// <summary>人类可读的证据描述，直接进日志。</summary>
        public string Detail { get; }

        /// <summary>附加的结构化数据（距离、速度等），便于后续调参。</summary>
        public IReadOnlyDictionary<string, float> Metrics { get; }

        public Violation(
            ViolationKind kind,
            Severity severity,
            int playerId,
            string playerName,
            float timestamp,
            string detail,
            Dictionary<string, float> metrics = null)
        {
            Kind = kind;
            Severity = severity;
            PlayerId = playerId;
            PlayerName = playerName ?? "?";
            Timestamp = timestamp;
            Detail = detail ?? string.Empty;
            Metrics = metrics ?? EmptyMetrics;
        }

        private static readonly IReadOnlyDictionary<string, float> EmptyMetrics =
            new Dictionary<string, float>();

        /// <summary>
        /// 该证据的基础权重。严重度越高权重越大，
        /// 但刻意不让 Critical 直接到 1.0——避免单条证据一票否决。
        /// </summary>
        public float BaseWeight => Severity switch
        {
            Severity.Low => 0.10f,
            Severity.Medium => 0.30f,
            Severity.High => 0.65f,
            Severity.Critical => 0.90f,
            _ => 0.10f,
        };

        public override string ToString() =>
            $"[{Severity}] {Kind} player={PlayerName}({PlayerId}) t={Timestamp:F1} :: {Detail}";
    }
}
