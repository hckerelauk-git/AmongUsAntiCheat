using System;
using System.Collections.Generic;

namespace ApexCheatEnder.Core
{
    /// <summary>某一时刻对某个玩家的观测快照。全部为值类型，避免采样时产生垃圾回收压力。</summary>
    public struct PlayerSnapshot
    {
        public float Time;
        public GameVec2 Position;
        public bool IsDead;
        public bool InVent;

        /// <summary>
        /// 处于管道 / 梯子 / 移动平台等会产生合法大位移的状态。
        /// 这些状态下位移远超正常走路速度，必须豁免瞬移与超速判定。
        /// </summary>
        public bool InSpecialMovement;
        public bool IsImpostor;

        /// <summary>角色是否被允许使用通风管（Role.CanVent）。Viper 为 true。</summary>
        public bool CanVent;

        /// <summary>上面那个 CanVent 是否取到了有效值。false 时不得据此判罚。</summary>
        public bool RoleKnown;

        public bool CanMove;
        public bool InMeeting;

        /// <summary>相对上一帧的位移。由 Tracker 填充。</summary>
        public float DeltaDistance;

        /// <summary>相对上一帧的时间差。由 Tracker 填充。</summary>
        public float DeltaTime;

        /// <summary>瞬时速度。</summary>
        public float Speed => DeltaTime > 0.0001f ? DeltaDistance / DeltaTime : 0f;
    }

    /// <summary>
    /// 单个玩家的持续观测状态。
    ///
    /// 这里承担两个职责：
    ///   1. 保存运动学历史，供超速/瞬移判定使用；
    ///   2. 记录「合法传送」的时间戳，把网络抖动和真实作弊区分开——
    ///      这是整套行为检测里最关键的降误报机制。
    /// </summary>
    public sealed class PlayerTrack
    {
        public int PlayerId { get; }
        public string Name { get; set; }

        /// <summary>本回合第一次被观测到的时间。</summary>
        public float FirstSeenTime { get; set; }

        /// <summary>
        /// 上一帧与当前帧的快照。
        /// 刻意用字段而不是属性——PlayerSnapshot 是 struct，
        /// 通过属性访问无法就地修改其成员（CS1612）。
        /// </summary>
        public PlayerSnapshot Previous;
        public PlayerSnapshot Current;

        public bool HasPrevious { get; set; }

        /// <summary>最近一次合法传送的时刻（RpcSnapTo / 回合开始 / 通风管 / 会议重置）。</summary>
        public float LastLegalTeleportTime { get; set; } = float.NegativeInfinity;

        /// <summary>连续超速命中次数。达到阈值才升级为规则命中，用于压制网络抖动。</summary>
        public int ConsecutiveSpeedStrikes { get; set; }

        /// <summary>本回合累计的瞬移证据次数，用于判断是否为惯犯。</summary>
        public int TeleportStrikeCount { get; set; }

        /// <summary>
        /// 最近一次击杀的目标玩家号。
        ///
        /// 用来识别「同一次击杀被上报两次」：游戏里不可能杀同一个人两次
        /// （第二次时对方已经是尸体），所以「同凶手 + 同目标 + 极短间隔」
        /// 必然是同一个事件。没有这个判断，一次击杀会立刻变成冷却绕过的误报。
        /// </summary>
        public int LastKillVictimId { get; set; } = -1;

        /// <summary>最近一次击杀的时刻，用于冷却绕过判定。</summary>
        public float LastKillTime { get; set; } = float.NegativeInfinity;

        /// <summary>最近一次完成任务的时刻与位置，用于任务速度判定。</summary>
        public float LastTaskTime { get; set; } = float.NegativeInfinity;
        public GameVec2 LastTaskPosition { get; set; }

        /// <summary>连续任务速度异常次数。</summary>
        public int ConsecutiveTaskStrikes { get; set; }

        /// <summary>最近若干次位置采样，用于回溯性轨迹分析。</summary>
        public readonly Queue<PlayerSnapshot> History = new Queue<PlayerSnapshot>(64);

        /// <summary>
        /// 待确认的瞬移：本帧检测到「物理上不可能的位移」，但要等下一帧看落点稳不稳。
        ///
        /// 网络抖动与真实瞬移的区别就在这里：
        ///   抖动 → 位置跳过去，下一帧又跳回原处（客户端拿到权威位置后自我纠正）
        ///   瞬移 → 落点稳定，不会回去
        /// 立即上报会把前者全判成作弊，实测就出现过 11.77 单位的单帧跳变。
        /// </summary>
        public bool HasPendingTeleport { get; set; }
        public GameVec2 PendingFrom { get; set; }
        public float PendingDistance { get; set; }
        public float PendingDeltaTime { get; set; }

        /// <summary>上一次上报瞬移证据的时刻，用于去重——避免持续瞬移时刷屏。</summary>
        public float LastTeleportReportTime { get; set; } = float.NegativeInfinity;

        /// <summary>上一次上报穿墙证据的时刻，用于去重。</summary>
        public float LastWallClipReportTime { get; set; } = float.NegativeInfinity;

        /// <summary>上一次上报会议移动证据的时刻，用于去重。</summary>
        public float LastMeetingMoveReportTime { get; set; } = float.NegativeInfinity;

        /// <summary>本回合该玩家是否已经产生过任何高严重度证据。</summary>
        public bool FlaggedThisRound { get; set; }

        /// <summary>聊天消息时间戳滑动窗口，用于刷屏判定。</summary>
        public readonly Queue<float> ChatTimes = new Queue<float>(16);

        /// <summary>上一次上报聊天刷屏的时刻，用于去重。</summary>
        public float LastChatReportTime { get; set; } = float.NegativeInfinity;

        /// <summary>上一次上报「非法动作」类证据的时刻，用于去重（按类型区分由调用方处理）。</summary>
        public float LastActionReportTime { get; set; } = float.NegativeInfinity;

        /// <summary>本回合首次被观测到是否已经处于对局中（用于早会判定的基准）。</summary>
        public bool SeenInRound { get; set; }

        public PlayerTrack(int playerId, string name, float now)
        {
            PlayerId = playerId;
            Name = name ?? "?";
            FirstSeenTime = now;
            Current = new PlayerSnapshot { Time = now };
            Previous = Current;
        }

        /// <summary>压入新快照，并计算位移/时间差，同时维护历史队列。</summary>
        public void Push(PlayerSnapshot snapshot)
        {
            Previous = Current;
            Current = snapshot;
            HasPrevious = true;

            Current.DeltaTime = snapshot.Time - Previous.Time;
            Current.DeltaDistance = GameVec2.Distance(snapshot.Position, Previous.Position);

            History.Enqueue(snapshot);
            while (History.Count > 64) History.Dequeue();
        }

        /// <summary>
        /// 判断某个时刻是否处于合法传送豁免窗口内。
        /// 窗口取 0.35 秒——足够覆盖一次 RpcSnapTo 的同步延迟，
        /// 又不至于长到让作弊者用连续 SnapTo 掩盖瞬移。
        /// </summary>
        public bool IsInLegalTeleportWindow(float now, float window = 0.35f) =>
            now - LastLegalTeleportTime <= window;

        /// <summary>
        /// 会议期间「连续」超容差移动的采样次数。
        ///
        /// 单次大位移不能判定 —— 入会时全员会被传送到会议桌，那是一次性尖峰。
        /// 要求连续两次以上，才能真正区分「传送」和「一直在动」。
        /// </summary>
        public int ConsecutiveMeetingMoveStrikes { get; set; }

        /// <summary>
        /// 连续处于墙体内部的采样次数。
        ///
        /// 单次采样可能是擦着桌子/控制台边缘的误判；真的穿墙会持续待在里面。
        /// </summary>
        public int ConsecutiveWallStrikes { get; set; }

        /// <summary>回合切换时清空所有回合级状态，但保留累计统计。</summary>
        public void ResetForNewRound(float now)
        {
            FirstSeenTime = now;
            HasPrevious = false;
            ConsecutiveSpeedStrikes = 0;
            ConsecutiveTaskStrikes = 0;
            LastKillTime = float.NegativeInfinity;
            LastKillVictimId = -1;
            LastTaskTime = float.NegativeInfinity;
            LastLegalTeleportTime = float.NegativeInfinity;
            HasPendingTeleport = false;
            ConsecutiveMeetingMoveStrikes = 0;
            ConsecutiveWallStrikes = 0;
            LastChatReportTime = float.NegativeInfinity;
            LastActionReportTime = float.NegativeInfinity;
            ChatTimes.Clear();
            FlaggedThisRound = false;
            SeenInRound = false;
            History.Clear();
            Current = new PlayerSnapshot { Time = now };
            Previous = Current;
        }
    }

    /// <summary>所有玩家的追踪器容器。</summary>
    public sealed class PlayerTracker
    {
        private readonly Dictionary<int, PlayerTrack> _tracks = new Dictionary<int, PlayerTrack>(16);

        public IReadOnlyDictionary<int, PlayerTrack> Tracks => _tracks;

        public PlayerTrack GetOrCreate(int playerId, string name, float now)
        {
            if (_tracks.TryGetValue(playerId, out var track))
            {
                if (!string.IsNullOrEmpty(name)) track.Name = name;
                return track;
            }
            var created = new PlayerTrack(playerId, name, now);
            _tracks[playerId] = created;
            return created;
        }

        public bool TryGet(int playerId, out PlayerTrack track) => _tracks.TryGetValue(playerId, out track);

        /// <summary>标记一次合法传送，使该玩家在豁免窗口内不被判为瞬移。</summary>
        public void MarkLegalTeleport(int playerId, float now)
        {
            if (_tracks.TryGetValue(playerId, out var track))
                track.LastLegalTeleportTime = now;
        }

        /// <summary>标记一次合法传送（按玩家名，用于拿不到 PlayerId 的场合）。</summary>
        public void MarkLegalTeleportByName(string name, float now)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (var t in _tracks.Values)
            {
                if (string.Equals(t.Name, name, StringComparison.Ordinal)) t.LastLegalTeleportTime = now;
            }
        }

        public void Forget(int playerId) => _tracks.Remove(playerId);

        public void ResetAll(float now)
        {
            foreach (var t in _tracks.Values) t.ResetForNewRound(now);
        }

        public void Clear() => _tracks.Clear();
    }
}
