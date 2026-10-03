using System;
using System.Collections.Generic;
using System.Text;

namespace ApexCheatEnder.Core
{
    // 仅提供本地提示信号，不产生证据、不拦截消息，也不执行任何处置。
    internal sealed class ChatRepeatNoticePolicy
    {
        internal const int MaxTrackedSenders = 32;
        internal const int MaxTextLength = 512;
        internal const int RepeatThreshold = 3;
        internal const double WindowSeconds = 10;
        internal const double CooldownSeconds = 10;
        private readonly Dictionary<int, SenderState> _senders = new Dictionary<int, SenderState>();
        private double _lastTime = double.NegativeInfinity;

        private sealed class SenderState
        {
            internal string Text;
            internal int Count;
            internal double Started;
            internal double LastSeen;
            internal double LastNotice = double.NegativeInfinity;
        }

        internal int TrackedSenderCount => _senders.Count;

        internal void Reset()
        {
            _senders.Clear();
            _lastTime = double.NegativeInfinity;
        }

        internal bool TryNotice(int senderId, string text, bool enabled, double now, int threshold = RepeatThreshold)
        {
            if (!enabled)
            {
                Reset();
                return false;
            }
            if (double.IsNaN(now) || double.IsInfinity(now) || senderId < 0 ||
                string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return false;
            string normalized;
            try { normalized = text.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant(); }
            catch (ArgumentException) { return false; }
            if (normalized.Length == 0 || normalized.Length > MaxTextLength) return false;
            if (now < _lastTime) Reset();
            _lastTime = now;

            if (!_senders.TryGetValue(senderId, out var state))
            {
                if (_senders.Count >= MaxTrackedSenders)
                {
                    var oldestId = -1;
                    var oldestTime = double.PositiveInfinity;
                    foreach (var pair in _senders)
                    {
                        if (pair.Value.LastSeen < oldestTime)
                        {
                            oldestId = pair.Key;
                            oldestTime = pair.Value.LastSeen;
                        }
                    }
                    _senders.Remove(oldestId);
                }
                state = new SenderState();
                _senders.Add(senderId, state);
            }
            state.LastSeen = now;
            if (!string.Equals(state.Text, normalized, StringComparison.Ordinal) || now - state.Started > WindowSeconds)
            {
                state.Text = normalized;
                state.Started = now;
                state.Count = 1;
                return false;
            }
            threshold = Math.Max(3, Math.Min(10, threshold));
            state.Count = Math.Min(10, state.Count + 1);
            if (state.Count < threshold || now - state.LastNotice < CooldownSeconds) return false;
            state.LastNotice = now;
            return true;
        }
    }
}
