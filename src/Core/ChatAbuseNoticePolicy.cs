using System;
using System.Text;

namespace ApexCheatEnder.Core
{
    // 仅用于本地疑似提示，不产生 Violation，也不参与玩家判定。
    internal sealed class ChatAbuseNoticePolicy
    {
        /// <summary>
        /// 默认词表。
        ///
        /// 收录原则：**针对人的辱骂**。
        /// 纯感叹词（卧槽、我操、他妈、妈的）刻意不收 —— 那是口头禅，
        /// 不是骂人，收进来只会天天误报。
        /// 如需增加词条，编辑 apex.cheat.ender.cfg 中的「触发关键词」。
        /// </summary>
        internal const string DefaultKeywords =
            "你妈死了,你妈死,死妈,你全家,死全家,全家死光,断子绝孙,不得好死," +
            "操你妈,草你妈,艹你妈,肏你妈,日你妈,干你妈,滚你妈,你妈的,去你妈的,草泥马,曹尼玛," +
            "你爹死了,你爸死了," +
            "傻逼,煞笔,沙比,傻狗,沙雕,智障,弱智,脑残,脑瘫,低能儿,白痴,蠢货,蠢猪," +
            "贱人,贱货,婊子,骚货,烂货,狗东西,狗娘养,狗杂种,畜生,杂种,野种," +
            "王八蛋,龟儿子,混蛋,屁眼,疯狗,死狗,去死吧,赶紧去死";
        internal const double DurationSeconds = 1.2;
        internal const double CooldownSeconds = 10;
        private double _lastShown = double.NegativeInfinity;

        /// <summary>
        /// 关键词前面紧邻这些字样时，视为「引用 / 否定 / 劝阻」，不提示。
        ///
        /// 中文没有词边界，纯子串匹配必然把「别说傻逼」「他骂我傻逼」「举报这个傻逼」
        /// 这类输入同样计入 —— 是误报的主要来源。
        /// 只在这些字样**紧贴**关键词时才豁免，不扩大范围，避免漏掉真的辱骂。
        /// </summary>
        private static readonly string[] BenignPrefixes =
        {
            "别说", "别叫", "别喊", "别骂", "不要", "不许", "不准", "禁止",
            "不骂", "举报", "屏蔽", "他骂", "他叫", "骂我", "说我", "谁骂",
        };

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
                    var boundaryOk = !RequiresBoundary(keyword) || IsBoundary(normalizedText, start, keyword.Length);
                    if (boundaryOk && !HasBenignPrefix(normalizedText, start)) return true;
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

        /// <summary>
        /// 关键词是否必须落在词边界上。
        ///
        /// 只对「含 ASCII 字母或数字」的关键词要求边界 —— 用来避免
        /// target 命中 targeting 这类英文子串误报。
        ///
        /// 中文**不能**要求边界：中文没有词分隔符，加上边界限制后
        /// 「前缀傻逼后缀」这种最常见的骂法会被整体拒绝，
        /// 长度 ≤2 的中文词更是永远匹配不上，词表等于摆设。
        /// </summary>
        /// <summary>关键词前面紧贴的字样是否属于「引用 / 否定」。</summary>
        private static bool HasBenignPrefix(string text, int start)
        {
            for (var i = 0; i < BenignPrefixes.Length; i++)
            {
                var prefix = Normalize(BenignPrefixes[i]);
                if (prefix.Length == 0) continue;
                if (start < prefix.Length) continue;
                if (string.CompareOrdinal(text, start - prefix.Length, prefix, 0, prefix.Length) == 0) return true;
            }
            return false;
        }

        private static bool RequiresBoundary(string keyword)
        {
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
