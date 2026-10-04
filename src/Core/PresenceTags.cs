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

        /// <summary>Amethyst 用户标记。</summary>
        public const string AmethystDefault = "💜AME用户💜";
    }
}
