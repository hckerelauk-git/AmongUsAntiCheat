namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 名字标记的默认文案。
    ///
    /// 单独放一个不依赖任何游戏类型的文件里，是因为
    /// <c>AntiCheatConfig</c> 会引用这些默认值，而配置类要被测试工程直接编译。
    /// 若把常量留在 <c>AcePresence</c> / <c>AmethystPresence</c> 里，
    /// 就会把 Hazel、Unity 的依赖一起拖进测试工程，测试直接编译不过。
    /// </summary>
    internal static class PresenceTags
    {
        /// <summary>ACE 用户标记。</summary>
        public const string AceDefault = "😱ACE用户😱";

        /// <summary>
        /// Amethyst 用户标记。
        ///
        /// 用粉心 💗 而不是原来的紫心 💜 —— 与 Amethyst 客户端自己的用户标识观感一致，
        /// 用户第一眼就认得出来。
        /// </summary>
        public const string AmethystDefault = "💗AME用户💗";

        /// <summary>
        /// Amethyst 用户名字的粉色（TMP 富文本用的十六进制，不带 #）。
        ///
        /// 用富文本给整段名字上色，而不是改 <c>TextMeshPro.color</c>：
        /// 名字颜色由游戏每帧自己写，改 Graphic 的颜色会被立刻覆盖、导致闪烁；
        /// 富文本是文本内容的一部分，游戏重写文本时才需要重刷，不会闪。
        /// </summary>
        public const string AmethystNameHex = "FF8FD0";

        /// <summary>
        /// 高风险标记。命中确定性规则，或同一条规则被重复确认多次。
        ///
        /// **抓到就必须标在头上。** 打游戏的时候没人会去翻日志 ——
        /// 日志是事后取证用的，当场能看见的只有名字标记。
        /// </summary>
        public const string HighRiskMark = "⛔高危";

        /// <summary>可疑标记（命中规则但证据还不够确定）。</summary>
        public const string SuspiciousMark = "⚠可疑";

        /// <summary>高风险名字色（红）。</summary>
        public const string HighRiskNameHex = "FF5555";

        /// <summary>可疑名字色（橙）。</summary>
        public const string SuspiciousNameHex = "FFB020";

        /// <summary>没装任何模组的玩家标记。</summary>
        public const string VanillaDefault = "原版玩家";

        /// <summary>
        /// 旧版的默认文案（「原本玩家」是笔误，正确写法是「原版玩家」）。
        ///
        /// BepInEx 会把默认值写进 cfg 文件，之后**改默认值对已有配置无效** ——
        /// 用户配置里存的是这一串。用它做一次性迁移判断，见 AntiCheatConfig。
        /// </summary>
        public const string LegacyVanillaTag = "原本玩家";

        /// <summary>
        /// 旧版的 Amethyst 标记（紫心）。
        ///
        /// BepInEx 会把默认值写进 cfg 文件，之后**改默认值对已有配置无效** ——
        /// 用户配置里存的是这一串。用它做一次性迁移判断，见 AntiCheatConfig。
        /// </summary>
        public const string LegacyAmethystTag = "💜AME用户💜";
    }
}
