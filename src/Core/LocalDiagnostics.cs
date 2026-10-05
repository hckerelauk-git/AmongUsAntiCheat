using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ApexCheatEnder.Core
{
    internal sealed class LocalEventHistory
    {
        internal const int Capacity = 256;
        private readonly Queue<Violation> _items = new Queue<Violation>(Capacity);
        public long TotalCount { get; private set; }
        public int Count => _items.Count;
        public void Add(Violation item)
        {
            if (item == null) return;
            if (_items.Count == Capacity) _items.Dequeue();
            // 历史是受限的快照，不保留任意长度文本或外部字典。
            var metrics = new Dictionary<string, float>();
            var inspected = 0;
            foreach (var pair in item.Metrics)
            {
                if (inspected++ == 16) break;
                metrics[Limit(pair.Key, 40)] = pair.Value;
            }
            _items.Enqueue(new Violation(item.Kind, item.Severity, item.PlayerId,
                Limit(item.PlayerName, 64), item.Timestamp, Limit(item.Detail, 512), metrics));
            TotalCount++;
        }
        internal static string Limit(string value, int max) => value == null ? "" : value.Length <= max ? value : value.Substring(0, max);
        public Violation[] LatestFirst()
        {
            var items = _items.ToArray();
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                items[i] = new Violation(item.Kind, item.Severity, item.PlayerId, item.PlayerName,
                    item.Timestamp, item.Detail, new Dictionary<string, float>(item.Metrics));
            }
            Array.Reverse(items);
            return items;
        }
        public string SafeExport(double fps)
        {
            var output = new StringBuilder("ACE 本地诊断（不含昵称、聊天、配置或路径）\n");
            output.Append("FPS=").Append(fps.ToString("F1", CultureInfo.InvariantCulture))
                .Append("; total=").Append(TotalCount).Append("; retained=").Append(Count).Append('\n');
            foreach (var item in _items)
                output.Append(item.Timestamp.ToString("F2", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(item.PlayerId).Append('\t').Append(item.Kind).Append('\t').Append(item.Severity).Append('\n');
            return output.ToString();
        }
    }

    internal sealed class DoubleClickConfirmation
    {
        private double _until = double.NegativeInfinity;
        private double _started = double.PositiveInfinity;
        public bool Armed(double now) => now >= _started && now <= _until && !double.IsNaN(now) && !double.IsInfinity(now);
        public void Cancel() => _until = double.NegativeInfinity;
        public bool Confirm(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) return false;
            if (Armed(now)) { Cancel(); return true; }
            _started = now;
            _until = now + 5;
            return false;
        }
    }

    internal static class RuleGroups
    {
        public static readonly string[] Names = { "移动", "动作", "会议", "消息", "网络", "静态", "名单" };
        public static string Default(ViolationKind kind)
        {
            switch (kind)
            {
                case ViolationKind.Teleport: case ViolationKind.SpeedHack: case ViolationKind.WallClip: return Names[0];
                case ViolationKind.MoveDuringMeeting: case ViolationKind.IllegalMeetingAction: case ViolationKind.EarlyMeeting: return Names[2];
                case ViolationKind.ChatFlood: case ViolationKind.IllegalChat: case ViolationKind.IllegalName: return Names[3];
                case ViolationKind.InvalidRpc: case ViolationKind.StateDesync: case ViolationKind.OversizedPacket: return Names[4];
                case ViolationKind.KnownCheatPlugin: case ViolationKind.UnknownPlugin: case ViolationKind.MemoryTamper: return Names[5];
                // 名单单独一组：它不是「检测到的行为」，是事先认定的人，混进「静态」会看不出来
                case ViolationKind.BannedPlayer: return Names[6];
                default: return Names[1];
            }
        }
        public static string Resolve(ViolationKind kind, string mapping)
        {
            if (mapping != null && mapping.Length <= 8192)
                foreach (var entry in mapping.Split(';'))
                {
                    var pair = entry.Split('=');
                    if (pair.Length == 2 && pair[0].Trim() == kind.ToString() && Array.IndexOf(Names, pair[1].Trim()) >= 0)
                        return pair[1].Trim();
                }
            return Default(kind);
        }
        public static string Cycle(ViolationKind selected, string mapping)
        {
            var next = Names[(Array.IndexOf(Names, Resolve(selected, mapping)) + 1) % Names.Length];
            var entries = new List<string>();
            foreach (ViolationKind kind in Enum.GetValues(typeof(ViolationKind)))
                entries.Add(kind + "=" + (kind == selected ? next : Resolve(kind, mapping)));
            return string.Join(";", entries);
        }
        public static Violation[] Filter(LocalEventHistory history, int group, string mapping)
        {
            var items = history.LatestFirst();
            if (group <= 0 || group > Names.Length) return items;
            return Array.FindAll(items, item => Resolve(item.Kind, mapping) == Names[group - 1]);
        }
        public static string Defaults()
        {
            var entries = new List<string>();
            foreach (ViolationKind kind in Enum.GetValues(typeof(ViolationKind))) entries.Add(kind + "=" + Default(kind));
            return string.Join(";", entries);
        }
    }
}
