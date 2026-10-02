using System;
using System.Collections.Generic;
using ApexCheatEnder.Core;

namespace ApexCheatEnder.Tests
{
    /// <summary>
    /// 零依赖测试 runner。
    ///
    /// 约定：每个 Test* 方法用 Assert 系列做断言，失败即抛异常，
    /// 由 Main 捕获并计入失败数；全部通过时退出码 0，否则 1。
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;
        private static readonly List<string> Failures = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("Apex Cheat Ender · 核心逻辑测试");
            Console.WriteLine(new string('-', 56));

            Run("GameVec2：距离与向量运算", TestGameVec2);
            Run("PlayerTrack：快照位移/时间差计算", TestPlayerTrackDelta);
            Run("PlayerTrack：合法传送豁免窗口", TestLegalTeleportWindow);
            Run("PlayerTrack：回合重置清空回合级状态", TestPlayerTrackReset);
            Run("PlayerTracker：按 Id 取或建轨迹", TestTracker);
            Run("Violation：严重度到基础权重的映射", TestViolationWeight);
            Run("PlayerVerdict：风险等级四态判定", TestEvaluateLevel);
            Run("PlayerVerdict：证据累加与 Critical 计数", TestAddEvidence);
            Run("PlayerVerdict：Reset 清空本局命中", TestVerdictReset);

            Console.WriteLine(new string('-', 56));
            Console.WriteLine($"通过 {_passed} ／ 失败 {_failed}");

            if (_failed > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败详情：");
                foreach (var f in Failures) Console.WriteLine("  ✗ " + f);
                return 1;
            }

            Console.WriteLine("全部通过 ✓");
            return 0;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("  ✓ " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Failures.Add($"{name} → {ex.Message}");
                Console.WriteLine("  ✗ " + name);
            }
        }

        // ================= 断言 =================

        private static void True(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        private static void Eq(float expected, float actual, string msg, float eps = 0.0001f)
        {
            if (Math.Abs(expected - actual) > eps)
                throw new Exception($"{msg}：期望 {expected}，实际 {actual}");
        }

        private static void Eq(int expected, int actual, string msg)
        {
            if (expected != actual)
                throw new Exception($"{msg}：期望 {expected}，实际 {actual}");
        }

        private static void Eq<T>(T expected, T actual, string msg) where T : struct
        {
            if (!expected.Equals(actual))
                throw new Exception($"{msg}：期望 {expected}，实际 {actual}");
        }

        // ================= GameVec2 =================

        private static void TestGameVec2()
        {
            var a = new GameVec2(0f, 0f);
            var b = new GameVec2(3f, 4f);

            Eq(5f, GameVec2.Distance(a, b), "3-4-5 直角三角形斜边");
            Eq(25f, GameVec2.SqrDistance(a, b), "平方距离");
            Eq(25f, b.SqrMagnitude, "向量自身平方长度");
            Eq(5f, b.Magnitude, "向量长度");

            var sum = a + b;
            Eq(3f, sum.X, "加法 X");
            Eq(4f, sum.Y, "加法 Y");

            var diff = b - a;
            Eq(3f, diff.X, "减法 X");

            // 中点在 0.5 处
            var mid = GameVec2.Lerp(a, b, 0.5f);
            Eq(1.5f, mid.X, "插值 X");
            Eq(2f, mid.Y, "插值 Y");

            // 零向量
            Eq(0f, GameVec2.Zero.X, "Zero.X");
            Eq(0f, GameVec2.Distance(a, a), "同点距离为 0");
        }

        // ================= PlayerTrack =================

        private static void TestPlayerTrackDelta()
        {
            var track = new PlayerTrack(1, "P1", 0f);
            True(!track.HasPrevious, "初始不应有上一帧");

            track.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f) });
            True(track.HasPrevious, "Push 后应标记有上一帧");

            // 1 秒移动 3-4-5 → 位移 5，速度 5
            track.Push(new PlayerSnapshot { Time = 1f, Position = new GameVec2(3f, 4f) });

            Eq(5f, track.Current.DeltaDistance, "位移应为 5");
            Eq(1f, track.Current.DeltaTime, "时间差应为 1");
            Eq(5f, track.Current.Speed, "速度应为 5");
        }

        private static void TestLegalTeleportWindow()
        {
            var track = new PlayerTrack(1, "P1", 0f);

            // 初始：LastLegalTeleportTime 为负无穷 → 任何时刻都不在豁免窗口内
            True(!track.IsInLegalTeleportWindow(100f), "未标记时不应处于豁免窗口");

            track.LastLegalTeleportTime = 10f;

            True(track.IsInLegalTeleportWindow(10.2f), "0.2s 后应在默认 0.35s 窗口内");
            // 注意：不要拿「正好等于窗口边界」的值做断言。
            // 10.35f - 10f 在 float 下是 0.35000038，比 0.35f 略大，
            // 边界比较会因浮点误差翻转。测边界两侧的安全距离即可。
            True(track.IsInLegalTeleportWindow(10.34f), "0.34s 应仍在窗口内");
            True(!track.IsInLegalTeleportWindow(10.36f), "0.36s 应已出窗口");
            True(!track.IsInLegalTeleportWindow(11f), "1s 后应已出窗口");

            // 自定义窗口
            True(track.IsInLegalTeleportWindow(11f, 1.5f), "自定义 1.5s 窗口内");
        }

        private static void TestPlayerTrackReset()
        {
            var track = new PlayerTrack(1, "P1", 0f);
            track.Push(new PlayerSnapshot { Time = 0f, Position = new GameVec2(0f, 0f) });
            track.Push(new PlayerSnapshot { Time = 1f, Position = new GameVec2(10f, 0f) });
            track.ConsecutiveSpeedStrikes = 5;
            track.ConsecutiveTaskStrikes = 3;
            track.FlaggedThisRound = true;
            track.LastKillTime = 5f;
            track.LastTaskTime = 6f;
            track.LastLegalTeleportTime = 7f;

            track.ResetForNewRound(100f);

            Eq(0, track.ConsecutiveSpeedStrikes, "超速计数应清零");
            Eq(0, track.ConsecutiveTaskStrikes, "任务计数应清零");
            True(!track.FlaggedThisRound, "本局标记应清除");
            True(!track.HasPrevious, "应清掉上一帧");
            Eq(100f, track.FirstSeenTime, "FirstSeenTime 应更新为新回合时间");
            True(float.IsNegativeInfinity(track.LastKillTime), "击杀时间应重置为负无穷");
            True(float.IsNegativeInfinity(track.LastLegalTeleportTime), "豁免时间应重置为负无穷");
            Eq(0, track.History.Count, "历史队列应清空");
        }

        private static void TestTracker()
        {
            var tracker = new PlayerTracker();

            var a = tracker.GetOrCreate(7, "Alpha", 0f);
            var again = tracker.GetOrCreate(7, "Alpha", 1f);
            True(ReferenceEquals(a, again), "同一 Id 应返回同一实例");

            // 名字可被更新
            tracker.GetOrCreate(7, "AlphaRenamed", 2f);
            True(a.Name == "AlphaRenamed", "名字应被新值覆盖");

            // 空名字不覆盖已有名字
            tracker.GetOrCreate(7, "", 3f);
            True(a.Name == "AlphaRenamed", "空名字不应覆盖");

            True(tracker.TryGet(7, out var found) && ReferenceEquals(found, a), "TryGet 应命中");
            True(!tracker.TryGet(999, out _), "不存在的 Id 应返回 false");

            tracker.MarkLegalTeleport(7, 42f);
            Eq(42f, a.LastLegalTeleportTime, "MarkLegalTeleport 应写入时间戳");

            tracker.Forget(7);
            True(!tracker.TryGet(7, out _), "Forget 后应查不到");

            // Clear 清空全部
            tracker.GetOrCreate(1, "X", 0f);
            tracker.GetOrCreate(2, "Y", 0f);
            tracker.Clear();
            Eq(0, tracker.Tracks.Count, "Clear 后应为空");
        }

        // ================= Violation =================

        private static void TestViolationWeight()
        {
            Eq(0.10f, Make(Severity.Low).BaseWeight, "Low 权重");
            Eq(0.30f, Make(Severity.Medium).BaseWeight, "Medium 权重");
            Eq(0.65f, Make(Severity.High).BaseWeight, "High 权重");
            Eq(0.90f, Make(Severity.Critical).BaseWeight, "Critical 权重");

            // Critical 刻意不到 1.0：避免单条证据一票否决
            True(Make(Severity.Critical).BaseWeight < 1.0f, "Critical 权重不应达到 1.0");

            // 空 metrics 不应为 null
            var v = Make(Severity.High);
            True(v.Metrics != null, "Metrics 不应为 null");
            Eq(0, v.Metrics.Count, "未传 metrics 时应为空集合");

            // 玩家名兜底
            var noName = new Violation(ViolationKind.Teleport, Severity.High, 1, null, 0f, "d");
            True(noName.PlayerName == "?", "玩家名为 null 时应兜底为 ?");
        }

        private static Violation Make(Severity s) =>
            new Violation(ViolationKind.Teleport, s, 1, "P", 0f, "detail");

        // ================= PlayerVerdict =================

        private static void TestEvaluateLevel()
        {
            var v = new PlayerVerdict { PlayerId = 1, Name = "P" };

            Eq(RiskLevel.Normal, v.EvaluateLevel(), "零命中应为 Normal");

            // 仅 High 命中 → Suspicious
            v.AddEvidence(Make(Severity.High));
            Eq(RiskLevel.Suspicious, v.EvaluateLevel(), "非确定性命中应为 Suspicious");

            // 出现 Critical → HighRisk
            v.AddEvidence(Make(Severity.Critical));
            Eq(RiskLevel.HighRisk, v.EvaluateLevel(), "有 Critical 应为 HighRisk");

            // AI 确认 → Confirmed（优先级高于 Critical）
            v.AiConfirmed = true;
            Eq(RiskLevel.Confirmed, v.EvaluateLevel(), "AI 确认应为 Confirmed 且优先于 HighRisk");

            // AI 取消确认后回到 HighRisk
            v.AiConfirmed = false;
            Eq(RiskLevel.HighRisk, v.EvaluateLevel(), "取消 AI 确认后应回落到 HighRisk");
        }

        private static void TestAddEvidence()
        {
            var v = new PlayerVerdict { PlayerId = 1, Name = "P" };
            Eq(0, v.EvidenceCount, "初始命中数为 0");
            Eq(0, v.CriticalCount, "初始 Critical 数为 0");

            v.AddEvidence(Make(Severity.Low));
            v.AddEvidence(Make(Severity.Medium));
            Eq(2, v.EvidenceCount, "两次普通命中后应为 2");
            Eq(0, v.CriticalCount, "非 Critical 不应计入 CriticalCount");

            v.AddEvidence(Make(Severity.Critical));
            Eq(3, v.EvidenceCount, "再加一条应为 3");
            Eq(1, v.CriticalCount, "Critical 应计入 CriticalCount");

            // null 不应崩、不应计数
            v.AddEvidence(null);
            Eq(3, v.EvidenceCount, "null 证据不应计数");

            Eq(3, v.Evidence.Count, "证据链应保留 3 条（供申诉说明）");
        }

        private static void TestVerdictReset()
        {
            var v = new PlayerVerdict { PlayerId = 1, Name = "P" };
            v.AddEvidence(Make(Severity.Critical));
            v.Warned = true;
            v.KickIssued = true;
            v.BanRequested = true;

            v.Reset();

            Eq(0, v.EvidenceCount, "Reset 后命中数应清零");
            Eq(0, v.CriticalCount, "Reset 后 Critical 数应清零");
            True(!v.Warned, "Reset 后 Warned 应清除");
            True(!v.KickIssued, "Reset 后 KickIssued 应清除");
            True(!v.BanRequested, "Reset 后 BanRequested 应清除");
            Eq(0, v.Evidence.Count, "Reset 后证据链应清空");
            Eq(RiskLevel.Normal, v.EvaluateLevel(), "Reset 后判定应回到 Normal");
        }
    }
}
