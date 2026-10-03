using System;
using System.Text;

namespace ApexCheatEnder.Core
{
    // 仅用于本地疑似提示，不产生 Violation，也不参与玩家判定。
    internal sealed class ChatAbuseNoticePolicy
    {
        internal const string DefaultKeywords = "你妈死了,操你妈,傻逼";
        internal const double DurationSeconds = 1.2;
        internal const double CooldownSeconds = 10;
        private double _lastShown = double.NegativeInfinity;

        internal static bool Matches(string text, string keywords)
        {
            var normalizedText = Normalize(text);
            if (normalizedText.Length == 0 || string.IsNullOrWhiteSpace(keywords)) return false;
            foreach (var part in keywords.Split(new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var keyword = Normalize(part);
                if (keyword.Length == 0) continue;
                var start = normalizedText.IndexOf(keyword, StringComparison.Ordinal);
                while (start >= 0)
                {
                    if (!RequiresBoundary(keyword) || IsBoundary(normalizedText, start, keyword.Length)) return true;
                    start = normalizedText.IndexOf(keyword, start + keyword.Length, StringComparison.Ordinal);
                }
            }
            return false;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try { value = value.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { return string.Empty; }
            var builder = new StringBuilder(value.Length);
            var separator = false;
            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    if (separator && builder.Length > 0) builder.Append(' ');
                    builder.Append(char.ToUpperInvariant(character));
                    separator = false;
                }
                else
                {
                    separator = true;
                }
            }
            return builder.ToString().Trim();
        }

        private static bool RequiresBoundary(string keyword)
        {
            if (keyword.Length <= 2) return true;
            foreach (var character in keyword)
                if (character < 128 && char.IsLetterOrDigit(character)) return true;
            return false;
        }

        private static bool IsBoundary(string text, int start, int length)
        {
            var before = start > 0 ? text[start - 1] : '\0';
            var afterIndex = start + length;
            var after = afterIndex < text.Length ? text[afterIndex] : '\0';
            return !char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after);
        }

        internal bool TryShow(string text, string keywords, bool enabled, double now)
        {
            if (!enabled || double.IsNaN(now) || double.IsInfinity(now) ||
                !Matches(text, keywords) || now - _lastShown < CooldownSeconds) return false;
            _lastShown = now;
            return true;
        }
    }
}
