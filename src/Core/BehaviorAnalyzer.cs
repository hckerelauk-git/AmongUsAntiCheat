using System;
using System.Collections.Generic;
using BepInEx.Logging;
using AmongUsAntiCheat.Config;

namespace AmongUsAntiCheat.Core
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
            if (distance >= _cfg.TeleportMinDistance.Value)
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

            // 非内鬼不能使用通风管
            if (!track.Current.IsImpostor && !track.Current.IsDead)
            {
                output.Add(new Violation(
                    ViolationKind.IllegalVent,
                    Severity.Critical,
                    track.PlayerId,
                    track.Name,
                    now,
                    "非内鬼玩家使用了通风管。",
                    new Dictionary<string, float>()));
                track.FlaggedThisRound = true;
                return;
            }

            // 距离校验：通风管必须在身边
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
    }
}
