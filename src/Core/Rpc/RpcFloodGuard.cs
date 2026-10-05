using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ApexCheatEnder.Core.Rpc
{
    /// <summary>
    /// RPC 速率保护。按 OwnerId 做滑动窗口统计，专门覆盖大厅、加载和开局阶段。
    /// 不把一次合法 RPC 当作弊；只有超过窗口阈值才提交一条证据，并短时间熔断重复告警。
    /// </summary>
    public sealed class RpcFloodGuard
    {
        private sealed class Window
        {
            public readonly Queue<float> Times = new Queue<float>(64);
            public float BlockUntil;
            public float LastFlag;
        }

        private readonly Dictionary<int, Window> _windows = new Dictionary<int, Window>(16);
        private readonly ManualLogSource _log;

        public RpcFloodGuard(ManualLogSource log) { _log = log; }

        public bool TryRecord(int ownerId, string playerName, string rpcName, float now, bool loading)
        {
            var cfg = AntiCheatRuntime.Config;
            if (cfg == null || cfg.RpcFloodDetection == null || !cfg.RpcFloodDetection.Value) return false;
            if (ownerId < 0) return false;

            if (!_windows.TryGetValue(ownerId, out var window))
            {
                window = new Window();
                _windows[ownerId] = window;
            }

            if (now < window.BlockUntil) return true;

            // **加载 / 大厅阶段给一倍的余量。**
            //
            // 现场：三人同时在「加载/大厅」阶段被判 61 个 RPC（阈值 60）——
            //   ① 超出只有 1~4 个，正好压在阈值上；
            //   ② 三个人**速率完全相同、最近类型也相同（164）**；
            //   ③ 阶段是加载/大厅。
            //
            // 三条合起来说明那是某个模组的**周期性广播**，不是洪水攻击：
            // 攻击不会三个人约好用同一个速率。而大厅阶段本来就有大量合法的
            // 位置 / 外观 / 准备状态同步，阈值卡在 60 会让正常流量刚好压线。
            var limit = Math.Max(5, cfg.RpcRateLimit?.Value ?? 60) * (loading ? 2 : 1);
            const float seconds = 10f;
            window.Times.Enqueue(now);
            while (window.Times.Count > 0 && now - window.Times.Peek() > seconds)
                window.Times.Dequeue();

            if (window.Times.Count <= limit) return false;

            window.BlockUntil = now + (loading ? 2.5f : 1.0f);
            if (now - window.LastFlag < 2f) return true;
            window.LastFlag = now;

            _log?.LogWarning($"[RPC洪水] {playerName}({ownerId}) 在 {seconds:F0} 秒内发送 {window.Times.Count} 个 RPC，最近类型：{rpcName}，阶段：{(loading ? "加载/大厅" : "对局")}");
            return true;
        }

        public void Reset(int ownerId) => _windows.Remove(ownerId);
        public void ResetAll() => _windows.Clear();
    }
}
