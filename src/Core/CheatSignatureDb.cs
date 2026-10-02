using System;
using System.Collections.Generic;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 单条作弊特征。匹配是「多字段或关系」：命中任意一个标记即算命中，
    /// 但不同字段的置信度不同，由 <see cref="Confidence"/> 调节。
    /// </summary>
    public sealed class CheatSignature
    {
        /// <summary>展示用名称，会写进日志。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>作弊软件所属类别。</summary>
        public string Category { get; set; } = "未分类";

        /// <summary>命中后的严重度。</summary>
        public Severity Severity { get; set; } = Severity.High;

        /// <summary>BepInEx 插件 GUID，精确匹配（最高置信度）。</summary>
        public string[] Guids { get; set; } = Array.Empty<string>();

        /// <summary>插件显示名 / 程序集文件名片段（不区分大小写）。</summary>
        public string[] NameMarkers { get; set; } = Array.Empty<string>();

        /// <summary>程序集内出现的类型全名片段。</summary>
        public string[] TypeMarkers { get; set; } = Array.Empty<string>();

        /// <summary>程序集内出现的关键字符串（用于识别混淆过的插件）。</summary>
        public string[] StringMarkers { get; set; } = Array.Empty<string>();

        /// <summary>该特征的说明，展示在面板上。</summary>
        public string Note { get; set; } = string.Empty;
    }

    /// <summary>
    /// 作弊软件特征库。
    ///
    /// 设计原则：
    /// 1. 只收录「确实破坏对局公平」的作弊软件（透视、瞬移、自动击杀、菜单注入）。
    /// 2. 单纯的玩法模组（TheOtherRoles / TownOfUs / Reactor 等）和工具模组
    ///    （反作弊、皮肤解锁、模组管理器）不进入黑名单，最多进灰名单记录。
    /// 3. 特征字段宁可少而准，不要多而滥——误伤正常玩家的代价远高于漏检。
    /// </summary>
    public static class CheatSignatureDb
    {
        /// <summary>黑名单：命中即产生证据。</summary>
        public static readonly List<CheatSignature> Blacklist = new List<CheatSignature>
        {
            new CheatSignature
            {
                Name = "Among Us Menu (AUM)",
                Category = "作弊菜单",
                Severity = Severity.Critical,
                Guids = new[] { "com.anonymus.aum", "amongusmenu", "aum" },
                NameMarkers = new[] { "AmongUsMenu", "AUM." },
                TypeMarkers = new[] { "AmongUsMenu.Main", "AmongUsMenu.Cheat", "AUM.Cheat", "AUM.Menu" },
                StringMarkers = new[] { "AmongUsMenu", "aum.cheats", "AUM_FEATURE" },
                Note = "最广为流传的 Among Us 作弊菜单，提供透视/瞬移/无冷却击杀等全套功能。",
            },
            new CheatSignature
            {
                Name = "MalumMenu",
                Category = "作弊菜单",
                Severity = Severity.Critical,
                Guids = new[] { "malummenu", "com.malum.menu" },
                NameMarkers = new[] { "MalumMenu", "Malum" },
                TypeMarkers = new[] { "MalumMenu.MenuUI", "MalumMenu.Cheats" },
                StringMarkers = new[] { "MalumMenu", "MalumCheats" },
                Note = "AUM 的前身分支，功能等价的作弊菜单。",
            },
            new CheatSignature
            {
                Name = "Minty",
                Category = "作弊菜单",
                Severity = Severity.Critical,
                Guids = new[] { "minty", "com.minty.client" },
                NameMarkers = new[] { "Minty" },
                TypeMarkers = new[] { "Minty.Menu", "Minty.Cheats" },
                StringMarkers = new[] { "MintyMenu" },
                Note = "服务端/客户端双端作弊套件。",
            },
            new CheatSignature
            {
                Name = "通用作弊菜单（Cheat Menu）",
                Category = "作弊菜单",
                Severity = Severity.Critical,
                Guids = new[] { "cheatmenu", "amonguscheat", "au.cheat", "aucheats" },
                NameMarkers = new[] { "CheatMenu", "AmongUsCheat", "AUMenu" },
                TypeMarkers = new[] { "CheatMenu", "CheatManager", "CheatGUI" },
                StringMarkers = new[] { "CheatMenu", "cheat_gui", "ToggleCheats" },
                Note = "泛化的作弊菜单命名特征。",
            },
            new CheatSignature
            {
                Name = "透视 / 雷达类 (ESP / Xray)",
                Category = "透视类",
                Severity = Severity.High,
                Guids = new[] { "esp", "xray", "wallhack", "amongusesp" },
                NameMarkers = new[] { "ESP", "Xray", "XRay", "Wallhack", "SeeRoles", "SeeImpostor" },
                TypeMarkers = new[] { "ESPRenderer", "XrayMode", "WallhackPatch", "RevealRoles" },
                StringMarkers = new[] { "showImpostors", "revealRoles", "wallhack" },
                Note = "透墙显示玩家/角色信息，破坏信息不对称这一核心机制。",
            },
            new CheatSignature
            {
                Name = "瞬移 / 穿墙类 (Noclip / Teleport)",
                Category = "运动类",
                Severity = Severity.Critical,
                Guids = new[] { "noclip", "teleport", "amongusteleport" },
                NameMarkers = new[] { "Noclip", "Teleport", "SpeedHack", "SpeedBoost" },
                TypeMarkers = new[] { "NoclipPatch", "TeleportCheat", "SpeedHackPatch" },
                StringMarkers = new[] { "noclip", "teleportTo", "speedMultiplier" },
                Note = "修改移动能力，直接破坏地图与追逐机制。",
            },
            new CheatSignature
            {
                Name = "自动击杀 / 宏类 (Aimbot / Trigger)",
                Category = "自动化",
                Severity = Severity.Critical,
                Guids = new[] { "aimbot", "autokill", "triggerbot", "autowin" },
                NameMarkers = new[] { "Aimbot", "AutoKill", "TriggerBot", "AutoWin", "KillAll" },
                TypeMarkers = new[] { "AimbotPatch", "AutoKillModule", "TriggerBot" },
                StringMarkers = new[] { "autoKill", "killAll", "aimbot" },
                Note = "自动化执行击杀，无需人工操作即可获胜。",
            },
            new CheatSignature
            {
                Name = "伪造身份 / 平台伪装 (Spoofer)",
                Category = "身份伪造",
                Severity = Severity.High,
                Guids = new[] { "spoofer", "au.spoofer", "fakeprofile" },
                NameMarkers = new[] { "Spoofer", "FakeProfile", "PlatformSpoof" },
                TypeMarkers = new[] { "PlatformSpoofer", "FakeFriendCode" },
                StringMarkers = new[] { "spoofPlatform", "fakeFriendCode" },
                Note = "伪造平台标签 / 好友码，用于绕过封禁或嫁祸他人。",
            },
        };

        /// <summary>
        /// 灰名单：出现即记录，但不计入规则命中。
        /// 用于观察未知模组，以及区分「玩法模组」与「作弊模组」。
        /// </summary>
        public static readonly List<CheatSignature> Graylist = new List<CheatSignature>
        {
            new CheatSignature
            {
                Name = "皮肤 / 饰品解锁",
                Category = "外观解锁",
                Severity = Severity.Low,
                NameMarkers = new[] { "UnlockAll", "UnlockSkins", "SkinChanger", "全皮肤解锁", "SkinUnlock" },
                TypeMarkers = new[] { "SkinUnlock", "UnlockAllCosmetics" },
                Note = "本地解锁外观，不影响对局公平性，仅作记录。",
            },
            new CheatSignature
            {
                Name = "模组管理器 / 开发工具",
                Category = "开发工具",
                Severity = Severity.Low,
                NameMarkers = new[] { "ModExplorer", "ModManager", "DebugMenu", "DeveloperTools" },
                TypeMarkers = new[] { "ModExplorer", "DevConsole" },
                Note = "合法的开发/调试工具，仅作记录。",
            },
        };

        /// <summary>
        /// 默认受信任名单。这些是常见的玩法模组与工具模组，
        /// 即使名字里带了敏感词（如 Unlock、Menu）也不应触发判定。
        /// 用户可以在此基础上通过配置追加。
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultTrustedMarkers = new List<string>
        {
            // —— 用户环境实际安装的工具/玩法模组 ——
            "Amethyst",          // 反作弊 / 对局增强
            "Reactor",           // 玩法模组框架
            "BetterAmongUs",
            "AmongUsRevamped",
            "ModExplorer",
            // —— 社区主流玩法模组 ——
            "TheOtherRoles", "TOR",
            "TownOfUs", "TOU",
            "TownOfHost", "TOH",
            "LasMonjas",
            "NebulaOnTheShip",
            "Submerged",
            "ExtremeRoles",
            "AllTheRoles",
            "FinalSuspect",
            "LevelImposter",
        };

        /// <summary>判断某个名称/程序集名是否属于默认受信任范围。</summary>
        public static bool IsDefaultTrusted(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (var marker in DefaultTrustedMarkers)
            {
                if (value.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
    }
}
