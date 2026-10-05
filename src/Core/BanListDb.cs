using System;
using System.Collections.Generic;
using System.Text;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 封禁名单里的一条。
    ///
    /// 匹配优先级：<see cref="Code"/>（好友码） &gt; <see cref="Puid"/>（平台用户 ID）
    /// &gt; <see cref="Name"/>（名字）。**前两者不随改名变化，名字可任意修改。**
    /// </summary>
    public sealed class BanEntry
    {
        /// <summary>展示用名字。只在日志和面板上出现，除非没有 Code / Puid 才参与匹配。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 好友码。Among Us 的格式是 <c>名字#四位数字</c>（如 <c>sloehind#4553</c>）。
        /// 这是**最可靠的匹配键** —— 名字能改，好友码改不了。
        /// </summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>平台用户 ID（ProductUserId）。比好友码还底层，改名换号都甩不掉。</summary>
        public string Puid { get; set; } = string.Empty;

        /// <summary>备注：为什么进名单。会原样显示在通知和监控面板上。</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>有没有可用的标识。没有的话只能靠名字碰运气。</summary>
        public bool HasId => !string.IsNullOrWhiteSpace(Code) || !string.IsNullOrWhiteSpace(Puid);
    }

    /// <summary>
    /// 内置封禁名单 + 用户追加名单。
    ///
    /// 设计参考 Amethyst 的 <c>AmethystAccess</c>（它也有同款功能，同样按
    /// FriendCode / ProductUserId 匹配），差别只有两点：
    ///   1. 我们**额外支持「只有名字」的条目** —— Amethyst 会直接丢掉这种条目，
    ///      但实际收录时经常拿不到 UID，全丢掉等于功能没用。
    ///   2. 匹配结果不直接踢人，而是走现有的证据链（记为确定级），
    ///      由「动手的方式」设置决定是警告 / 踢出 / 封禁。
    ///
    /// **只收录确实破坏对局、且能唯一定位的玩家。** 名字匹配天然可能误伤
    /// 同名的人，所以能用好友码就不要只写名字。
    /// </summary>
    public static class BanListDb
    {
        /// <summary>条目分隔符。</summary>
        public const char EntrySeparator = ';';

        /// <summary>字段分隔符。</summary>
        public const char FieldSeparator = '|';

        /// <summary>
        /// 内置名单。
        ///
        /// 收录标准：**必须有好友码或平台 ID** —— 只有名字的条目改名即失效，
        /// 除非对方名字本身够独特（如「鸟（繁体）」）。
        /// </summary>
        public static readonly IReadOnlyList<BanEntry> BuiltIn = new List<BanEntry>
        {
            new BanEntry
            {
                Name = "sloehind",
                Code = "sloehind#4553",
                Reason = "名字经常更换；炸躲猫猫房间；使用移动类外挂；低龄骚扰",
            },
            new BanEntry
            {
                Name = "鸟（繁体）",
                Reason = "在游戏中扰乱对局；发表分裂国家言论",
            },
            new BanEntry
            {
                Name = "B站星函星辰",
                Puid = "seniorhive8445",
                Reason = "经典房改 CD 玩躲猫猫；拿到房主后开局隐瞒自己的内鬼身份；" +
                         "被指出后恼羞成怒把全房踢出",
            },
            new BanEntry
            {
                Name = "黑龙",
                Code = "linkfair#3286",
                Reason = "无故骚扰他人、素质低下、满口烂梗；输了就退；" +
                         "使用 aur 乱改房间设置；被击杀即封禁对方",
            },
            new BanEntry
            {
                Name = "Øㄒ乇乃卂几Ø",
                Code = "mirestoic#8178",
                Reason = "使用「加载中开会」炸房外挂",
            },
        };

        /// <summary>
        /// 校验好友码格式：<c>非空名字#正好四位数字</c>。
        ///
        /// **逐字照搬 Amethyst 的 <c>IsValidFriendCode</c>** —— 这是游戏自己的格式，
        /// 不是我们发明的规则，照抄比自创可靠。
        /// </summary>
        public static bool IsValidFriendCode(string fc)
        {
            if (string.IsNullOrWhiteSpace(fc)) return false;

            var hash = fc.LastIndexOf('#');
            // '#' 必须在中间（前面得有名字），且后面正好四位
            if (hash <= 0 || fc.Length - hash - 1 != 4) return false;

            for (var i = hash + 1; i < fc.Length; i++)
                if (!char.IsDigit(fc[i])) return false;

            return true;
        }

        /// <summary>
        /// 归一化：去空白、转小写、全角括号转半角。
        ///
        /// 全角括号同样需要归一化：名单条目「鸟（繁体）」使用全角括号，
        /// 但玩家可能输入「鸟(繁体)」—— 不做归一化则无法匹配。
        /// </summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (c == '\u200b' || c == '\ufeff') continue;   // 零宽字符

                sb.Append(c switch
                {
                    '（' => '(',
                    '）' => ')',
                    '［' => '[',
                    '］' => ']',
                    _ => char.ToLowerInvariant(c),
                });
            }
            return sb.ToString();
        }

        /// <summary>
        /// 往配置串里追加一条。
        ///
        /// 已有同标识的条目不重复添加 —— 重复添加只会让配置串越来越长，
        /// 而且用户看到「加过了但没提示」会以为没生效。
        /// 没有可匹配标识（好友码 / 平台 ID 都空）时返回原样：
        /// 这种条目本来就会被 Check 忽略，加进去只会造成「明明加了却不生效」的困惑。
        /// </summary>
        public static string AppendEntry(string existing, string name, string code, string puid, string reason)
        {
            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(puid))
                return existing;

            var list = Parse(existing);
            var nCode = Normalize(code);
            var nPuid = Normalize(puid);

            foreach (var e in list)
            {
                if (nCode.Length > 0 && string.Equals(Normalize(e.Code), nCode, StringComparison.Ordinal))
                    return existing;
                if (nPuid.Length > 0 && string.Equals(Normalize(e.Puid), nPuid, StringComparison.Ordinal))
                    return existing;
            }

            list.Add(new BanEntry
            {
                Name = string.IsNullOrWhiteSpace(name) ? (code.Length > 0 ? code : puid) : name,
                Code = code ?? string.Empty,
                Puid = puid ?? string.Empty,
                Reason = string.IsNullOrWhiteSpace(reason) ? "手动封禁" : reason,
            });

            return Format(list);
        }

        /// <summary>判断一个玩家是否命中名单（不含在线名单）。</summary>
        public static bool Check(string code, string puid, string playerName,
            IReadOnlyList<BanEntry> extra, out BanEntry hit)
            => Check(code, puid, playerName, extra, null, out hit);

        /// <summary>
        /// 判断一个玩家是否命中名单。
        ///
        /// 匹配顺序：在线名单 → 内置名单 → 自定义条目。
        /// 在线名单优先，因为它的理由字段最完整，命中后展示的信息更充分。
        /// </summary>
        /// <param name="code">玩家的好友码（可为空）。</param>
        /// <param name="puid">玩家的平台用户 ID（可为空）。</param>
        /// <param name="playerName">玩家的显示名。</param>
        /// <param name="extra">自定义条目，可为 null。</param>
        /// <param name="remote">在线名单条目，可为 null。</param>
        /// <param name="hit">命中的条目。</param>
        public static bool Check(string code, string puid, string playerName,
            IReadOnlyList<BanEntry> extra, IReadOnlyList<BanEntry> remote, out BanEntry hit)
        {
            hit = null;

            var nCode = Normalize(code);
            var nPuid = Normalize(puid);
            var nName = Normalize(playerName);

            if (remote != null && Match(remote, nCode, nPuid, nName, out hit)) return true;
            if (Match(BuiltIn, nCode, nPuid, nName, out hit)) return true;
            if (extra != null && Match(extra, nCode, nPuid, nName, out hit)) return true;

            return false;
        }

        private static bool Match(IReadOnlyList<BanEntry> list,
            string nCode, string nPuid, string nName, out BanEntry hit)
        {
            hit = null;
            if (list == null) return false;

            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null) continue;

                // 1) 好友码：最可靠，改名甩不掉
                if (!string.IsNullOrEmpty(e.Code) && nCode.Length > 0)
                {
                    var nEntryCode = Normalize(e.Code);

                    if (string.Equals(nEntryCode, nCode, StringComparison.Ordinal))
                    {
                        hit = e;
                        return true;
                    }

                    // 兼容两种表示：端点可能只存码（CCCC3333），
                    // 而游戏内的 FriendCode 可能带名字前缀（名字#CCCC3333）。
                    // 仅当较短一方长度 >= 6 时才做包含匹配 ——
                    // 码太短时包含匹配会退化成子串碰撞，宁可漏判也不误判。
                    var shorter = nEntryCode.Length <= nCode.Length ? nEntryCode : nCode;
                    var longer = nEntryCode.Length <= nCode.Length ? nCode : nEntryCode;
                    if (shorter.Length >= 6 && longer.IndexOf(shorter, StringComparison.Ordinal) >= 0)
                    {
                        hit = e;
                        return true;
                    }
                }

                // 2) 平台 ID
                if (!string.IsNullOrEmpty(e.Puid) &&
                    string.Equals(Normalize(e.Puid), nPuid, StringComparison.Ordinal) &&
                    nPuid.Length > 0)
                {
                    hit = e;
                    return true;
                }

                // 3) 名字：只在**没有** Code / Puid 时才用。
                //
                // 为什么不把名字当兜底全量比对：名单里有「黑龙」这种两字常用词，
                // 只要有人叫「黑龙王」就会误伤。有 UID 就只认 UID。
                if (!e.HasId && !string.IsNullOrEmpty(e.Name))
                {
                    var nEntry = Normalize(e.Name);
                    if (nEntry.Length > 0 && nName.Length > 0 &&
                        (string.Equals(nEntry, nName, StringComparison.Ordinal) ||
                         nName.IndexOf(nEntry, StringComparison.Ordinal) >= 0))
                    {
                        hit = e;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 解析用户追加的名单。
        ///
        /// 格式：<c>名字|好友码|平台ID|备注</c>，多条用 <c>;</c> 分隔。
        /// 后三个字段可以留空，但**至少要有好友码或平台 ID**，
        /// 否则一条「只写名字」的条目会误伤所有同名玩家 —— 直接丢弃并记一条日志。
        /// </summary>
        public static List<BanEntry> Parse(string csv)
        {
            var list = new List<BanEntry>();
            if (string.IsNullOrWhiteSpace(csv)) return list;

            var chunks = csv.Split(EntrySeparator);
            foreach (var chunk in chunks)
            {
                if (string.IsNullOrWhiteSpace(chunk)) continue;

                var parts = chunk.Split(FieldSeparator);
                var name = parts.Length >= 1 ? Clean(parts[0]) : string.Empty;
                var code = parts.Length >= 2 ? Clean(parts[1]) : string.Empty;
                var puid = parts.Length >= 3 ? Clean(parts[2]) : string.Empty;
                var reason = parts.Length >= 4 ? Clean(parts[3]) : string.Empty;

                if (name.Length == 0 && code.Length == 0 && puid.Length == 0) continue;

                list.Add(new BanEntry
                {
                    Name = name,
                    Code = code,
                    Puid = puid,
                    Reason = reason.Length > 0 ? reason : "自定义名单",
                });
            }

            return list;
        }

        /// <summary>把名单序列化回配置字符串。</summary>
        public static string Format(IReadOnlyList<BanEntry> list)
        {
            if (list == null || list.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            for (var i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(EntrySeparator);
                sb.Append(Escape(list[i].Name)).Append(FieldSeparator)
                  .Append(Escape(list[i].Code)).Append(FieldSeparator)
                  .Append(Escape(list[i].Puid)).Append(FieldSeparator)
                  .Append(Escape(list[i].Reason));
            }
            return sb.ToString();
        }

        /// <summary>去掉会破坏分隔符的字符 —— 配置是纯文本，分隔符串味了整份名单就废了。</summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace(FieldSeparator, '/').Replace(EntrySeparator, ',');
        }

        private static string Clean(string value) => (value ?? string.Empty).Trim();
    }
}
