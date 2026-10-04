using System.Collections.Generic;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 模组指纹库：**靠自定义 RPC 的 callId 识别对方装了什么模组**。
    ///
    /// ────────── 为什么用 RPC 指纹而不是握手 ──────────
    ///
    /// 参考主流实现（NitroAntiCheat 的 `CheatClients`、其上游 BetterAmongUs 的
    /// 作弊客户端处理器）得出的结论：
    ///
    ///   · 握手协议（像 Amethyst 那样广播 GUID）**只能认出愿意自报家门的模组**，
    ///     作弊端恰恰最不愿意自报。而且对方不发，你就永远不知道。
    ///   · **RPC 指纹是被动观察**：只要对方发过自定义 RPC，就能认出来，
    ///     不需要对方配合，也躲不掉 —— 除非它一个自定义包都不发（那就等于没用模组）。
    ///
    /// 判定依据：游戏本体的 `RpcCalls` 枚举**最大到 67**（`SpiritGuideMessage`）。
    /// 超出这个范围的 callId，原版客户端**永远不会发送**，所以一定是模组发的。
    ///
    /// 数据来源：
    ///   · NitroAntiCheat `src/CheatClients.cs`（AUM / SickoMenu / KillNetwork 的指纹）
    ///   · Amethyst 互认包（callId 50，需包内 marker 二次确认）
    ///   · 本插件自身（callId 240）
    /// </summary>
    internal static class ModFingerprintDb
    {
        /// <summary>
        /// 原版 <c>RpcCalls</c> 的最大值。
        ///
        /// 超过它的 callId 一定是模组自定义的。这个数字随游戏版本可能变，
        /// 但目前版本（含 SpiritGuideMessage = 67）是准的；
        /// 宁可把范围算大一点（漏判）也不要算小（误判正常 RPC）。
        /// </summary>
        internal const int VanillaRpcMax = 67;

        internal sealed class Entry
        {
            /// <summary>友好名称，显示在玩家头上。</summary>
            public string Name;

            /// <summary>BepInEx 插件 GUID。未知时为 null。</summary>
            public string Guid;

            /// <summary>是否已知作弊端 —— 用于标红。</summary>
            public bool IsCheat;
        }

        /// <summary>
        /// callId → 模组。
        ///
        /// 只收「能确定归属」的 ID。认不出的自定义 callId 不在这里，
        /// 由 <see cref="ModFingerprint"/> 按原始 ID 显示，方便日后补库。
        /// </summary>
        internal static readonly Dictionary<byte, Entry> ByCallId = new Dictionary<byte, Entry>
        {
            // 本插件自己
            { 240, new Entry { Name = "Apex Cheat Ender", Guid = "apex.cheat.ender" } },

            // Amethyst 的互认包。注意它复用了原版范围内的 50，
            // 所以单看 callId 不够 —— 必须由 ModFingerprint 再验包里的 marker 字符串。
            { 50, new Entry { Name = "Amethyst", Guid = "amethyst.mod" } },

            // 以下三条来自 NitroAntiCheat 的实测指纹
            { 85, new Entry { Name = "AmongUsMenu (AUM)", Guid = "com.anonymus.aum", IsCheat = true } },
            { 101, new Entry { Name = "AmongUsMenu 聊天", Guid = "com.anonymus.aum", IsCheat = true } },
            { 164, new Entry { Name = "SickoMenu", Guid = "sicko.menu", IsCheat = true } },
            { 250, new Entry { Name = "KillNetwork", Guid = "killnetwork", IsCheat = true } },
            { 119, new Entry { Name = "KillNetwork 聊天", Guid = "killnetwork", IsCheat = true } },
        };

        /// <summary>
        /// GUID → 友好名称。
        ///
        /// 用户要求：**库里查得到就用名字，查不到就直接显示 GUID 原文**。
        /// 这样遇到没见过的模组不会显示成空白，而是一串可以直接拿去搜的 GUID。
        /// </summary>
        internal static readonly Dictionary<string, string> ByGuid = new Dictionary<string, string>
        {
            // 自报家门的模组
            { "apex.cheat.ender", "ACE" },
            { "amethyst.mod", "Amethyst" },

            // 常见模组（含 CheatSignatureDb 里的作弊端）
            { "com.anonymus.aum", "AmongUsMenu (AUM)" },
            { "amongusmenu", "AmongUsMenu (AUM)" },
            { "aum", "AmongUsMenu (AUM)" },
            { "malummenu", "MalumMenu" },
            { "com.malum.menu", "MalumMenu" },
            { "minty", "Minty" },
            { "com.minty.client", "Minty" },
            { "sicko.menu", "SickoMenu" },
            { "killnetwork", "KillNetwork" },

            // 常见非作弊模组（参考 Amethyst 的 HasPlugin 探测名单）
            { "BetterPingDisplay", "BetterPingDisplay" },
            { "FPSOptimizer", "FPS & Network Optimizer" },

            // 大型内容模组
            { "com.townofus.plugin", "TownOfUs" },
            { "TownOfUs", "TownOfUs" },
            { "TheOtherRoles", "The Other Roles" },
            { "com.submerged", "Submerged" },
            { "LasMonjas", "Las Monjas" },
            { "Nebula", "Nebula on the Ship" },
            { "EndlessHostRoles", "Endless Host Roles" },
            { "TownOfHostEnhanced", "Town of Host: Enhanced" },
            { "Reactor", "Reactor" },
        };

        /// <summary>按 GUID 取显示名；库里没有就返回 GUID 原文。</summary>
        internal static string DisplayOf(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            return ByGuid.TryGetValue(guid, out var name) ? name : guid;
        }

        /// <summary>这个 callId 是否超出原版范围（即模组自定义）。</summary>
        internal static bool IsCustomCallId(int callId) => callId > VanillaRpcMax;
    }
}
