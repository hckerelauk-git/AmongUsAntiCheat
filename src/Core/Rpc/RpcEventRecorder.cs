using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AmongUsAntiCheat.Core
{
    /// <summary>
    /// 每玩家 RPC 事件环形缓冲。
    ///
    /// 调用方（Harmony 补丁 / 行为分析器）只管 <see cref="Record"/>，
    /// 触发方（AI 分析器）按需 <see cref="Snapshot"/>。
    /// </summary>
    public sealed class RpcEventRecorder
    {
        /// <summary>每位玩家最多保留的事件数（环形覆盖）。</summary>
        private const int CapacityPerPlayer = 32;

        private readonly Dictionary<int, Queue<RpcEvent>> _buffers = new Dictionary<int, Queue<RpcEvent>>(16);

        public void Record(int playerId, RpcEvent evt)
        {
            if (playerId < 0) return;
            if (!_buffers.TryGetValue(playerId, out var queue))
            {
                queue = new Queue<RpcEvent>(CapacityPerPlayer);
                _buffers[playerId] = queue;
            }
            queue.Enqueue(evt);
            while (queue.Count > CapacityPerPlayer) queue.Dequeue();
        }

        /// <summary>取走该玩家当前的全部事件（拷贝），并清空缓冲。</summary>
        public RpcEvent[] Drain(int playerId)
        {
            if (!_buffers.TryGetValue(playerId, out var queue) || queue.Count == 0)
                return System.Array.Empty<RpcEvent>();
            var arr = queue.ToArray();
            queue.Clear();
            return arr;
        }

        /// <summary>只读快照，不清空。</summary>
        public RpcEvent[] Snapshot(int playerId)
        {
            if (!_buffers.TryGetValue(playerId, out var queue) || queue.Count == 0)
                return System.Array.Empty<RpcEvent>();
            return queue.ToArray();
        }

        public void Forget(int playerId) => _buffers.Remove(playerId);

        public void Clear() => _buffers.Clear();

        /// <summary>便捷：从 Harmony 补丁里直接构造并记录一条事件。</summary>
        public void Record(int playerId, string type, float? cooldown = null, float? distance = null, string argsJson = null)
        {
            var evt = new RpcEvent
            {
                Time = Time.time,
                Type = type,
                ArgsJson = argsJson,
            };
            // 距离与冷却不是通用字段；为了避免过度膨胀，我们把它们塞进 ArgsJson。
            // 如果调用方已提供完整 ArgsJson（argsJson != null），不再附加。
            if (evt.ArgsJson == null && (cooldown.HasValue || distance.HasValue))
            {
                var sb = new StringBuilder("{");
                var first = true;
                if (distance.HasValue)
                {
                    if (!first) sb.Append(',');
                    sb.Append("\"distance\":").Append(distance.Value.ToString("F2"));
                    first = false;
                }
                if (cooldown.HasValue)
                {
                    if (!first) sb.Append(',');
                    sb.Append("\"cooldown_remaining\":").Append(cooldown.Value.ToString("F2"));
                }
                sb.Append('}');
                evt.ArgsJson = sb.ToString();
            }
            Record(playerId, evt);
        }
    }
}