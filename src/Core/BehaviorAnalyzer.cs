using System;
using System.Collections.Generic;
using BepInEx.Logging;
using ApexCheatEnder.Config;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 行为检测层：运动学与动作合法性分析。
    ///
    /// 这一层全部是「确定性算法 + 时间窗口」的组合，没有机器学习成分，
    /// 原因是反作弊判定必须可解释、可复现——被误判的玩家需要能看到具体是哪条规则、哪个数值触发。
    ///
    /// 降误报的三道闸门：
    ///   1. 合法传送豁免窗口（RpcSnapTo / 通风管 / 回合开始 / 会议重置）
    ///   2. 抖动容差（小于阈值的位移直接忽略）
    ///   3. 连续命中计数（单次异常不算，连续 N 次才升级为证据）
    /// </summary>
    public sealed class BehaviorAnalyzer
    {
        private readonly AntiCheatConfig _cfg;
        private readonly ManualLogSource _log;

        /// <summary>瞬移证据的重复上报冷却（秒）。</summary>
        private const float TeleportReportCooldown = 1.0f;

        /// <summary>穿墙证据的重复上报冷却（秒）。</summary>
        private const float WallClipReportCooldown = 2.0f;

        /// <summary>会议移动证据的重复上报冷却（秒）。</summary>
        private const float MeetingMoveReportCooldown = 1.0f;

        /// <summary>
        /// 瞬移判定的速度倍率：隐含速度必须超过「允许上限 × 该倍率」才算瞬移。
        /// 取 3 倍是为了给「速度上限本身估算偏低」留出余量，进一步压低误报。
        /// </summary>
        private const float TeleportSpeedFactor = 3f;

        /// <summary>
        /// 由 Unity 侧注入的墙体检测委托：给定世界坐标，返回是否位于墙体内。
        /// 用委托而不是直接引用 Physics2D，是为了让本类保持可独立测试。
        /// 为 null 时自动跳过穿墙检测。
        /// </summary>
        public Func<GameVec2, bool> IsInsideWall { get; set; }

        public BehaviorAnalyzer(AntiCheatConfig cfg, ManualLogSource log)
        {
            _cfg = cfg;
            _log = log;
        }

        // ==================================================================
        //  运动学分析
        // ==================================================================

        /// <summary>
        /// 对一次新的位置采样做运动学分析。
        /// 调用前提：<see cref="PlayerTrack.Push"/> 已经完成，Current/Previous 均已填充。
        /// </summary>
        /// <param name="track">玩家轨迹。</param>
        /// <param name="now">当前游戏时间。</param>
        /// <param name="maxAllowedSpeed">当前对局允许的最大移动速度（已含角色与设置修正）。</param>
        /// <param name="gracePeriod">回合开始后的宽限时长。</param>
        /// <param name="output">证据收集器。</param>
        public void AnalyzeMovement(
            PlayerTrack track,
            float now,
            float maxAllowedSpeed,
            float gracePeriod,
            List<Violation> output)
        {
            if (!track.HasPrevious) return;

            var cur = track.Current;
            var dt = cur.DeltaTime;

            // 时间没有推进（同一帧重复采样）——忽略
            if (dt <= 0.0001f) return;

            // 死亡玩家的移动是合法的（幽灵可自由移动），不参与运动学判定
            if (cur.IsDead)
            {
                track.ConsecutiveSpeedStrikes = 0;
                return;
            }

            // 通风管内部是合法的空间跳跃，直接重置计数并标记豁免
            if (cur.InVent)
            {
                track.ConsecutiveSpeedStrikes = 0;
                track.LastLegalTeleportTime = now;
                return;
            }

            // 会议期间位置本就不应变化，交给专门的会议判定处理
            if (cur.InMeeting)
            {
                track.ConsecutiveSpeedStrikes = 0;
                return;
            }

            // 回合开始宽限期：出生点传送、场景加载都会造成大位移
            if (now - track.FirstSeenTime < gracePeriod) return;

            // 处于合法传送豁免窗口内（刚收到 SnapTo 同步）
            if (track.IsInLegalTeleportWindow(now)) return;

            var distance = cur.DeltaDistance;

            // 抖动容差：小于该位移视为网络同步噪声
            var jitter = _cfg.PositionJitterTolerance.Value;
            if (distance < jitter)
            {
                DecaySpeedStrikes(track);
                return;
            }

            // ---------- 判定一：瞬移 ----------
            // 必须同时满足两个条件才算瞬移：
            //   ① 位移够大（超过 TeleportMinDistance）
            //   ② 该位移在当前耗时内物理上不可能完成（隐含速度远超上限）
            //
            // 只比距离是早期版本的 bug：采样间隔被拉长或游戏卡顿时，
            // 正常玩家一次也能"跳"出好几个单位，于是被误判成瞬移。
            // 加入 ② 之后，隐含速度会随 dt 一起回落，卡顿不再制造误报。
            var impliedSpeed = distance / dt;
            var impossibleSpeed = maxAllowedSpeed * TeleportSpeedFactor;
            if (distance >= _cfg.TeleportMinDistance.Value && impliedSpeed > impossibleSpeed)
            {
                ReportTeleport(track, now, distance, dt, output);
                track.ConsecutiveSpeedStrikes = 0;
                return;
            }

            // ---------- 判定二：持续超速 ----------
            var speed = distance / dt;
            if (speed > maxAllowedSpeed)
            {
                track.ConsecutiveSpeedStrikes++;

                if (track.ConsecutiveSpeedStrikes >= _cfg.SpeedStrikeCount.Value)
                {
                    output.Add(new Violation(
                        ViolationKind.SpeedHack,
                        Severity.High,
                        track.PlayerId,
                        track.Name,
                        now,
                        $"持续超速：{speed:F2} 单位/秒，超出允许上限 {maxAllowedSpeed:F2}，已连续 {track.ConsecutiveSpeedStrikes} 次采样。",
                        new Dictionary<string, float>
                        {
                            ["speed"] = speed,
                            ["max_speed"] = maxAllowedSpeed,
                            ["strikes"] = track.ConsecutiveSpeedStrikes,
                            ["distance"] = distance,
                            ["delta_time"] = dt,
                        }));

                    // 重置计数，避免持续超速时每次采样都产生一条证据
                    track.ConsecutiveSpeedStrikes = 0;
                    track.FlaggedThisRound = true;
                }
            }
            else
            {
                DecaySpeedStrikes(track);
            }

            // ---------- 判定三：穿墙（可选，开销较高） ----------
            if (_cfg.EnableWallClipCheck.Value && IsInsideWall != null)
            {
                if (now - track.LastWallClipReportTime >= WallClipReportCooldown && IsInsideWall(cur.Position))
                {
                    track.LastWallClipReportTime = now;
                    output.Add(new Violation(
                        ViolationKind.WallClip,
                        Severity.Medium,
                        track.PlayerId,
                        track.Name,
                        now,
                        $"位置 {cur.Position} 处于墙体碰撞体内部（穿墙）。",
                        new Dictionary<string, float>
                        {
                            ["x"] = cur.Position.X,
                            ["y"] = cur.Position.Y,
                        }));
                }
            }

            if (_cfg.VerboseLogging.Value)
            {
                _log.LogInfo(
                    $"[运动采样] {track.Name}({track.PlayerId}) 位移={distance:F3} dt={dt:F3} 速度={speed:F2}/{maxAllowedSpeed:F2}");
            }
        }

        private void ReportTeleport(PlayerTrack track, float now, float distance, float dt, List<Violation> output)
        {
            if (now - track.LastTeleportReportTime < TeleportReportCooldown) return;
            track.LastTeleportReportTime = now;
            track.TeleportStrikeCount++;
            track.FlaggedThisRound = true;

            // 首次瞬移留有余地（可能是极端网络抖动），重复出现直接定为确定性证据
            var severity = track.TeleportStrikeCount >= 2 ? Severity.Critical : Severity.High;

            output.Add(new Violation(
                ViolationKind.Teleport,
                severity,
                track.PlayerId,
                track.Name,
                now,
                $"瞬移：单次采样位移 {distance:F2} 单位（阈值 {_cfg.TeleportMinDistance.Value:F2}），耗时 {dt:F3} 秒。本回合第 {track.TeleportStrikeCount} 次。",
                new Dictionary<string, float>
                {
                    ["distance"] = distance,
                    ["delta_time"] = dt,
                    ["threshold"] = _cfg.TeleportMinDistance.Value,
                    ["strike_count"] = track.TeleportStrikeCount,
                    ["from_x"] = track.Previous.Position.X,
                    ["from_y"] = track.Previous.Position.Y,
                    ["to_x"] = track.Current.Position.X,
                    ["to_y"] = track.Current.Position.Y,
                }));
        }

        private static void DecaySpeedStrikes(PlayerTrack track)
        {
            if (track.ConsecutiveSpeedStrikes > 0) track.ConsecutiveSpeedStrikes--;
        }

        // ==================================================================
        //  击杀合法性
        // ==================================================================

        /// <summary>
        /// 校验一次击杀。在击杀动作发生时调用。
        /// </summary>
        /// <param name="killer">击杀者轨迹。</param>
        /// <param name="victim">受害者轨迹，可为 null（拿不到时只做冷却与角色校验）。</param>
        /// <param name="now">当前时间。</param>
        /// <param name="allowedKillDistance">当前设置允许的最大击杀距离。</param>
        /// <param name="killCooldown">当前角色的击杀冷却（秒）。</param>
        /// <param name="output">证据收集器。</param>
        public void AnalyzeKill(
            PlayerTrack killer,
            PlayerTrack victim,
            float now,
            float allowedKillDistance,
            float killCooldown,
            List<Violation> output)
        {
            if (killer == null) return;

            var tolerance = _cfg.KillDistanceTolerance.Value;

            // ---------- 角色校验：非内鬼执行击杀 ----------
            if (!killer.Current.IsImpostor && !killer.Current.IsDead)
            {
                output.Add(new Violation(
                    ViolationKind.KillWhileNotImpostor,
                    Severity.Critical,
                    killer.PlayerId,
                    killer.Name,
                    now,
                    "非内鬼阵营的玩家执行了击杀动作。",
                    new Dictionary<string, float>()));
                killer.FlaggedThisRound = true;
            }

            // ---------- 距离校验 ----------
            if (victim != null)
            {
                var distance = GameVec2.Distance(killer.Current.Position, victim.Current.Position);
                var limit = allowedKillDistance + tolerance;

                if (distance > limit)
                {
                    output.Add(new Violation(
                        ViolationKind.KillTooFar,
                        Severity.High,
                        killer.PlayerId,
                        killer.Name,
                        now,
                        $"超距击杀：与目标「{victim.Name}」相距 {distance:F2} 单位，允许上限 {limit:F2}。",
                        new Dictionary<string, float>
                        {
                            ["distance"] = distance,
                            ["limit"] = limit,
                            ["setting_distance"] = allowedKillDistance,
                        }));
                    killer.FlaggedThisRound = true;
                }
            }

            // ---------- 冷却校验 ----------
            if (!float.IsNegativeInfinity(killer.LastKillTime))
            {
                var interval = now - killer.LastKillTime;
                var minInterval = killCooldown - _cfg.KillCooldownTolerance.Value;

                if (minInterval > 0f && interval < minInterval)
                {
                    output.Add(new Violation(
                        ViolationKind.KillCooldownBypass,
                        Severity.High,
                        killer.PlayerId,
                        killer.Name,
                        now,
                        $"击杀冷却绕过：两次击杀间隔仅 {interval:F2} 秒，角色冷却为 {killCooldown:F2} 秒。",
                        new Dictionary<string, float>
                        {
                            ["interval"] = interval,
                            ["cooldown"] = killCooldown,
                        }));
                    killer.FlaggedThisRound = true;
                }
            }

            killer.LastKillTime = now;
        }

        // ==================================================================
        //  任务合法性
        // ==================================================================

        /// <summary>
        /// 校验一次任务完成。在任务提交时调用。
        /// </summary>
        /// <param name="track">玩家轨迹。</param>
        /// <param name="taskPosition">该任务点的世界坐标。</param>
        /// <param name="now">当前时间。</param>
        /// <param name="maxAllowedSpeed">当前对局允许的最大移动速度。</param>
        /// <param name="output">证据收集器。</param>
        public void AnalyzeTask(
            PlayerTrack track,
            GameVec2 taskPosition,
            float now,
            float maxAllowedSpeed,
            List<Violation> output)
        {
            if (track == null) return;

            // ---------- 远程任务：提交时人不在任务点附近 ----------
            var distanceToTask = GameVec2.Distance(track.Current.Position, taskPosition);
            if (distanceToTask > _cfg.RemoteTaskTolerance.Value)
            {
                output.Add(new Violation(
                    ViolationKind.RemoteTask,
                    Severity.High,
                    track.PlayerId,
                    track.Name,
                    now,
                    $"远程提交任务：玩家位于 {track.Current.Position}，任务点位于 {taskPosition}，相距 {distanceToTask:F2} 单位。",
                    new Dictionary<string, float>
                    {
                        ["distance_to_task"] = distanceToTask,
                        ["tolerance"] = _cfg.RemoteTaskTolerance.Value,
                    }));
                track.FlaggedThisRound = true;
            }

            // ---------- 任务速度：两次任务点之间的移动速度超过物理上限 ----------
            if (!float.IsNegativeInfinity(track.LastTaskTime))
            {
                var elapsed = now - track.LastTaskTime;
                var travelled = GameVec2.Distance(taskPosition, track.LastTaskPosition);
                var minRequired = travelled / Math.Max(maxAllowedSpeed, 0.01f) * _cfg.TaskSpeedTolerance.Value;

                if (elapsed > 0.01f && elapsed < minRequired)
                {
                    track.ConsecutiveTaskStrikes++;
                    output.Add(new Violation(
                        ViolationKind.TaskTooFast,
                        track.ConsecutiveTaskStrikes >= 2 ? Severity.Critical : Severity.Medium,
                        track.PlayerId,
                        track.Name,
                        now,
                        $"任务速度异常：{travelled:F2} 单位距离仅耗时 {elapsed:F2} 秒，物理下限为 {minRequired:F2} 秒。",
                        new Dictionary<string, float>
                        {
                            ["elapsed"] = elapsed,
                            ["travelled"] = travelled,
                            ["min_required"] = minRequired,
                            ["strikes"] = track.ConsecutiveTaskStrikes,
                        }));
                    track.FlaggedThisRound = true;
                }
            }

            track.LastTaskTime = now;
            track.LastTaskPosition = taskPosition;
        }

        // ==================================================================
        //  会议期间移动
        // ==================================================================

        /// <summary>
        /// 校验会议期间的位置变化。会议中玩家不应发生位移。
        /// </summary>
        public void AnalyzeMeetingMovement(PlayerTrack track, float now, List<Violation> output)
        {
            if (track == null || !track.HasPrevious) return;
            if (track.Current.IsDead) return;
            if (now - track.LastMeetingMoveReportTime < MeetingMoveReportCooldown) return;

            var distance = track.Current.DeltaDistance;
            if (distance <= _cfg.MeetingMoveTolerance.Value) return;

            track.LastMeetingMoveReportTime = now;
            track.FlaggedThisRound = true;

            output.Add(new Violation(
                ViolationKind.MoveDuringMeeting,
                Severity.High,
                track.PlayerId,
                track.Name,
                now,
                $"会议期间发生位移 {distance:F2} 单位（容差 {_cfg.MeetingMoveTolerance.Value:F2}）。",
                new Dictionary<string, float>
                {
                    ["distance"] = distance,
                    ["tolerance"] = _cfg.MeetingMoveTolerance.Value,
                }));
        }

        // ==================================================================
        //  通风管合法性
        // ==================================================================

        /// <summary>校验一次通风管使用。</summary>
        /// <param name="track">玩家轨迹。</param>
        /// <param name="ventPosition">通风管世界坐标。</param>
        /// <param name="now">当前时间。</param>
        /// <param name="output">证据收集器。</param>
        public void AnalyzeVentUse(PlayerTrack track, GameVec2 ventPosition, float now, List<Violation> output)
        {
            if (track == null) return;

            // 非法使用通风管：按「角色能力」判定，而不是阵营。
            //
            // 历史教训：早期用 !IsImpostor 判断，把 Viper（船员阵营、Role.CanVent=true）
            // 和躲猫猫的 Seeker 一律判成 Critical 直接踢掉，是典型误杀。
            // 现在要求：① 角色能力已知 ② 该角色确实没有通风能力 ③ 人还活着。
            // 角色信息拿不到时一律放行（宁可漏报，不可冤判）。
            if (_cfg.VentNonImpostor.Value &&
                track.Current.RoleKnown && !track.Current.CanVent && !track.Current.IsDead)
            {
                output.Add(new Violation(
                    ViolationKind.IllegalVent,
                    Severity.Critical,
                    track.PlayerId,
                    track.Name,
                    now,
                    "角色不具备通风能力，却使用了通风管。",
                    new Dictionary<string, float>()));
                track.FlaggedThisRound = true;
                return;
            }

            // 距离校验：通风管必须在身边
            if (!_cfg.VentRemote.Value) return;

            var distance = GameVec2.Distance(track.Current.Position, ventPosition);
            var tolerance = _cfg.VentDistanceTolerance?.Value ?? _cfg.RemoteTaskTolerance.Value;
            if (distance > tolerance)
            {
                output.Add(new Violation(
                    ViolationKind.IllegalVent,
                    Severity.High,
                    track.PlayerId,
                    track.Name,
                    now,
                    $"远程使用通风管：与通风管相距 {distance:F2} 单位（容差 {tolerance:F2}）。",
                    new Dictionary<string, float>
                    {
                        ["distance"] = distance,
                        ["tolerance"] = tolerance,
                    }));
                track.FlaggedThisRound = true;
            }

            // 通风管本身是合法传送来源，标记豁免
            track.LastLegalTeleportTime = now;
        }

        // ==================================================================
        //  幽灵动作
        // ==================================================================

        /// <summary>已死亡玩家执行了只允许活人执行的动作。</summary>
        public void AnalyzeGhostAction(
            PlayerTrack track,
            float now,
            string action,
            List<Violation> output)
        {
            if (track == null || !track.Current.IsDead) return;

            output.Add(new Violation(
                ViolationKind.GhostAction,
                Severity.Critical,
                track.PlayerId,
                track.Name,
                now,
                $"已死亡玩家执行了活人动作：{action}。",
                new Dictionary<string, float>()));
            track.FlaggedThisRound = true;
        }

        // ==================================================================
        //  破坏系统
        // ==================================================================

        /// <summary>通用动作去重冷却：同一玩家在窗口内不重复上报同类动作。</summary>
        private const float ActionReportCooldown = 1.0f;

        /// <summary>
        /// 校验一次破坏系统触发。
        ///
        /// 破坏是内鬼专属能力，且会议期间不应发生。
        /// 角色信息不可用时**不判罚**——这是刻意的保守设计，避免把未知角色误伤。
        /// </summary>
        public void AnalyzeSabotage(
            PlayerTrack track,
            bool isImpostor,
            bool roleKnown,
            bool isDead,
            string systemName,
            bool inMeeting,
            float now,
            List<Violation> output)
        {
            if (track == null || isDead) return;
            if (!_cfg.SabotageCheck.Value) return;
            if (now - track.LastActionReportTime < ActionReportCooldown) return;

            // ① 会议期间触发破坏：所有人都被定在会议桌，物理上不可能
            if (inMeeting)
            {
                track.LastActionReportTime = now;
                track.FlaggedThisRound = true;
                output.Add(new Violation(
                    ViolationKind.SabotageDuringMeeting,
                    Severity.Critical,
                    track.PlayerId, track.Name, now,
                    $"会议进行中触发了破坏系统「{systemName}」。",
                    new Dictionary<string, float>()));
                return;
            }

            // ② 非内鬼触发破坏（仅在角色信息可读时判定）
            if (roleKnown && !isImpostor)
            {
                track.LastActionReportTime = now;
                track.FlaggedThisRound = true;
                output.Add(new Violation(
                    ViolationKind.SabotageWhileNotImpostor,
                    Severity.Critical,
                    track.PlayerId, track.Name, now,
                    $"不具备内鬼身份的玩家触发了破坏系统「{systemName}」。",
                    new Dictionary<string, float>()));
            }
        }

        /// <summary>破坏目标编号越界（伪造 / 改包）。</summary>
        public void AnalyzeSabotageTarget(
            PlayerTrack track, string systemName, int systemId, int validCount,
            float now, List<Violation> output)
        {
            if (track == null || validCount <= 0) return;
            if (!_cfg.SabotageCheck.Value) return;
            if (systemId >= 0 && systemId < validCount) return;

            output.Add(new Violation(
                ViolationKind.InvalidSabotageTarget,
                Severity.Critical,
                track.PlayerId, track.Name, now,
                $"破坏目标编号越界：{systemId}（合法范围 0~{validCount - 1}），系统名「{systemName}」。",
                new Dictionary<string, float>
                {
                    ["system_id"] = systemId,
                    ["valid_count"] = validCount,
                }));
            track.FlaggedThisRound = true;
        }

        // ==================================================================
        //  会议
        // ==================================================================

        /// <summary>
        /// 开局保护期内发起会议 / 报告尸体。
        /// 常见于刷屏骚扰，或想破坏开局的节奏。
        /// </summary>
        public void AnalyzeEarlyMeeting(
            PlayerTrack track, float roundElapsed, string action,
            float now, List<Violation> output)
        {
            if (track == null) return;
            if (!_cfg.BlockEarlyMeeting.Value) return;

            var grace = _cfg.EarlyMeetingGrace.Value;
            if (grace <= 0f || roundElapsed < 0f || roundElapsed >= grace) return;
            if (now - track.LastActionReportTime < ActionReportCooldown) return;

            track.LastActionReportTime = now;
            track.FlaggedThisRound = true;

            output.Add(new Violation(
                ViolationKind.EarlyMeeting,
                Severity.High,
                track.PlayerId, track.Name, now,
                $"开局 {roundElapsed:F1} 秒内发起{action}（保护期为 {grace:F0} 秒）。",
                new Dictionary<string, float>
                {
                    ["round_elapsed"] = roundElapsed,
                    ["grace"] = grace,
                }));
        }

        // ==================================================================
        //  聊天
        // ==================================================================

        /// <summary>
        /// 校验一条聊天消息：刷屏频率 + 内容合法性。
        /// </summary>
        public void AnalyzeChat(
            PlayerTrack track, string text, float now, List<Violation> output)
        {
            if (track == null) return;
            if (!_cfg.ChatCheck.Value) return;

            // ---------- 内容合法性 ----------
            if (string.IsNullOrEmpty(text))
            {
                output.Add(new Violation(
                    ViolationKind.IllegalChat,
                    Severity.Medium,
                    track.PlayerId, track.Name, now,
                    "发送了空聊天消息。",
                    new Dictionary<string, float>()));
                track.FlaggedThisRound = true;
            }
            else if (text.Length > 300)
            {
                output.Add(new Violation(
                    ViolationKind.IllegalChat,
                    Severity.High,
                    track.PlayerId, track.Name, now,
                    $"聊天消息过长（{text.Length} 字符），可能用于撑爆他人聊天框。",
                    new Dictionary<string, float> { ["length"] = text.Length }));
                track.FlaggedThisRound = true;
            }
            else if (ContainsControlChar(text))
            {
                output.Add(new Violation(
                    ViolationKind.IllegalChat,
                    Severity.High,
                    track.PlayerId, track.Name, now,
                    "聊天消息中包含控制字符。",
                    new Dictionary<string, float>()));
                track.FlaggedThisRound = true;
            }

            // ---------- 刷屏频率（10 秒滑动窗口） ----------
            const float Window = 10f;
            track.ChatTimes.Enqueue(now);
            while (track.ChatTimes.Count > 0 && now - track.ChatTimes.Peek() > Window)
                track.ChatTimes.Dequeue();

            var limit = _cfg.ChatRateLimit.Value;
            if (track.ChatTimes.Count <= limit) return;
            if (now - track.LastChatReportTime < 2f) return;

            track.LastChatReportTime = now;
            track.FlaggedThisRound = true;

            output.Add(new Violation(
                ViolationKind.ChatFlood,
                Severity.High,
                track.PlayerId, track.Name, now,
                $"{Window:F0} 秒内发送了 {track.ChatTimes.Count} 条聊天消息（上限 {limit} 条）。",
                new Dictionary<string, float>
                {
                    ["count"] = track.ChatTimes.Count,
                    ["limit"] = limit,
                    ["window"] = Window,
                }));
        }

        /// <summary>是否含控制字符（换行 / 制表 / 其它不可打印字符）。</summary>
        private static bool ContainsControlChar(string s)
        {
            foreach (var c in s)
            {
                if (char.IsControl(c) && c != '\u0000') return true;
            }
            return false;
        }

        // ==================================================================
        //  昵称
        // ==================================================================

        /// <summary>校验昵称合法性。</summary>
        public void AnalyzeName(
            PlayerTrack track, string rawName, float now, List<Violation> output)
        {
            if (track == null) return;
            if (!_cfg.NameCheck.Value) return;

            if (string.IsNullOrWhiteSpace(rawName))
            {
                output.Add(new Violation(
                    ViolationKind.IllegalName,
                    Severity.Medium,
                    track.PlayerId, track.Name, now,
                    "昵称为空或全为空白字符。",
                    new Dictionary<string, float>()));
                track.FlaggedThisRound = true;
                return;
            }

            var max = _cfg.NameMaxLength.Value;
            if (rawName.Length > max)
            {
                output.Add(new Violation(
                    ViolationKind.IllegalName,
                    Severity.High,
                    track.PlayerId, track.Name, now,
                    $"昵称过长（{rawName.Length} 字符，上限 {max}）。",
                    new Dictionary<string, float>
                    {
                        ["length"] = rawName.Length,
                        ["max"] = max,
                    }));
                track.FlaggedThisRound = true;
                return;
            }

            if (ContainsControlChar(rawName))
            {
                output.Add(new Violation(
                    ViolationKind.IllegalName,
                    Severity.High,
                    track.PlayerId, track.Name, now,
                    "昵称中包含控制字符（换行 / 制表等）。",
                    new Dictionary<string, float>()));
                track.FlaggedThisRound = true;
            }
        }

        // ==================================================================
        //  角色动作
        // ==================================================================

        /// <summary>校验变形动作：只有变形者能变形。</summary>
        public void AnalyzeShapeshift(
            PlayerTrack track, bool isShifter, bool roleKnown, bool isDead,
            float now, List<Violation> output)
        {
            if (track == null || isDead) return;
            if (!_cfg.RoleActionCheck.Value) return;
            if (!roleKnown || isShifter) return;   // 角色未知 → 放行

            output.Add(new Violation(
                ViolationKind.IllegalShapeshift,
                Severity.Critical,
                track.PlayerId, track.Name, now,
                "不具备变形能力的角色执行了变形。",
                new Dictionary<string, float>()));
            track.FlaggedThisRound = true;
        }

        /// <summary>校验保护动作：只有守护天使能保护。</summary>
        public void AnalyzeProtect(
            PlayerTrack track, bool isGuardian, bool roleKnown, bool isDead,
            float now, List<Violation> output)
        {
            if (track == null || isDead) return;
            if (!_cfg.RoleActionCheck.Value) return;
            if (!roleKnown || isGuardian) return;  // 角色未知 → 放行

            output.Add(new Violation(
                ViolationKind.IllegalProtect,
                Severity.Critical,
                track.PlayerId, track.Name, now,
                "不具备保护能力的角色执行了保护。",
                new Dictionary<string, float>()));
            track.FlaggedThisRound = true;
        }

        // ==================================================================
        //  通风管 / 滑索 进阶
        // ==================================================================

        /// <summary>
        /// 校验一次通风管操作（PerformVentOp）。
        /// 覆盖：伪造管道编号、会议期间使用。
        /// </summary>
        public void AnalyzeVentOp(
            PlayerTrack track, int ventId, bool? validVentId, bool inMeeting,
            float now, List<Violation> output)
        {
            if (track == null) return;

            // ① 伪造管道编号
            if (_cfg.VentForgedId.Value && validVentId == false)
            {
                output.Add(new Violation(
                    ViolationKind.VentForgedId,
                    Severity.Critical,
                    track.PlayerId, track.Name, now,
                    $"使用了不存在的通风管编号 {ventId}。",
                    new Dictionary<string, float> { ["vent_id"] = ventId }));
                track.FlaggedThisRound = true;
                return;
            }

            // ② 会议期间使用通风管
            if (_cfg.VentDuringMeeting.Value && inMeeting)
            {
                if (now - track.LastActionReportTime < ActionReportCooldown) return;
                track.LastActionReportTime = now;
                track.FlaggedThisRound = true;

                output.Add(new Violation(
                    ViolationKind.VentDuringMeeting,
                    Severity.Critical,
                    track.PlayerId, track.Name, now,
                    "会议进行中使用了通风管。",
                    new Dictionary<string, float> { ["vent_id"] = ventId }));
            }
        }

        /// <summary>
        /// 非房主强制把他人踢出通风管（BootImpostorFromVent 类 RPC）。
        /// 这是权限提升类漏洞，普通玩家不应有该能力。
        /// </summary>
        public void AnalyzeVentForce(
            PlayerTrack track, bool senderIsHost, string method,
            float now, List<Violation> output)
        {
            if (track == null) return;
            if (!_cfg.VentForceOther.Value) return;
            if (senderIsHost) return;   // 房主自己触发是合法的

            output.Add(new Violation(
                ViolationKind.VentForceOther,
                Severity.Critical,
                track.PlayerId, track.Name, now,
                $"非房主通过 {method} 强制把其他玩家踢出通风管。",
                new Dictionary<string, float>()));
            track.FlaggedThisRound = true;
        }

        /// <summary>滑索使用异常：会议期间使用。</summary>
        public void AnalyzeZipline(
            PlayerTrack track, bool inMeeting, bool isDead, float now, List<Violation> output)
        {
            if (track == null || isDead) return;
            if (!_cfg.ZiplineAbuse.Value) return;
            if (!inMeeting) return;

            if (now - track.LastActionReportTime < ActionReportCooldown) return;
            track.LastActionReportTime = now;
            track.FlaggedThisRound = true;

            output.Add(new Violation(
                ViolationKind.ZiplineAbuse,
                Severity.High,
                track.PlayerId, track.Name, now,
                "会议进行中使用了滑索。",
                new Dictionary<string, float>()));
        }

        // ==================================================================
        //  网络
        // ==================================================================

        /// <summary>收到异常大的数据包。</summary>
        public void AnalyzeOversizedPacket(
            int playerId, string playerName, int size, int limit,
            float now, List<Violation> output)
        {
            if (!_cfg.OversizedPacketCheck.Value) return;
            if (size <= limit) return;

            output.Add(new Violation(
                ViolationKind.OversizedPacket,
                Severity.High,
                playerId, playerName, now,
                $"收到异常大的数据包（{size} 字节，上限 {limit}）。",
                new Dictionary<string, float>
                {
                    ["size"] = size,
                    ["limit"] = limit,
                }));
        }
    }
}
