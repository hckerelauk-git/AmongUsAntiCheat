using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using AmongUsAntiCheat.Config;

namespace AmongUsAntiCheat.Core
{
    /// <summary>
    /// 单个已加载插件的描述信息。刻意与 BepInEx 的 PluginInfo 解耦，
    /// 方便后续做单元测试与日志序列化。
    /// </summary>
    public sealed class LoadedPluginInfo
    {
        public string Guid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string EntryType { get; set; } = string.Empty;

        public override string ToString() =>
            $"{Name} v{Version} [{Guid}] {(string.IsNullOrEmpty(Location) ? "" : Path.GetFileName(Location))}";
    }

    /// <summary>
    /// 静态检测层：扫描 BepInEx 已加载插件，比对作弊特征库。
    ///
    /// 这是整个反作弊里置信度最高的一层——作弊软件必须先被注入进进程才能生效，
    /// 而注入必然在 BepInEx 的插件表里留下痕迹。所以这一层几乎没有误报。
    ///
    /// 匹配策略（从高置信到低置信）：
    ///   1. GUID 精确匹配        —— 作者改不了 GUID 而不破坏依赖
    ///   2. 插件名 / 程序集名片段 —— 改名的常见绕过手段
    ///   3. 类型名片段（扫文件）  —— 插件改名但代码没改
    ///   4. 字符串特征（扫文件）  —— 插件整体混淆但内部字符串还在
    /// </summary>
    public sealed class ModScanner
    {
        private readonly AntiCheatConfig _cfg;
        private readonly ManualLogSource _log;

        /// <summary>扫描单个插件文件时的读取上限，避免被超大文件拖慢启动。</summary>
        private const int MaxScanBytes = 16 * 1024 * 1024;

        public ModScanner(AntiCheatConfig cfg, ManualLogSource log)
        {
            _cfg = cfg;
            _log = log;
        }

        /// <summary>本轮扫描的完整结果，供 UI 展示。</summary>
        public sealed class ScanReport
        {
            public List<LoadedPluginInfo> AllPlugins { get; } = new List<LoadedPluginInfo>();
            public List<string> TrustedPlugins { get; } = new List<string>();
            public List<string> GrayPlugins { get; } = new List<string>();
            public List<Violation> Violations { get; } = new List<Violation>();
            public List<string> Errors { get; } = new List<string>();
        }

        public ScanReport Scan()
        {
            var report = new ScanReport();
            var trustedGuids = _cfg.GetTrustedGuids();
            var trustedNames = _cfg.GetTrustedNames();

            List<LoadedPluginInfo> plugins;
            try
            {
                plugins = CollectLoadedPlugins();
            }
            catch (Exception ex)
            {
                report.Errors.Add($"读取 BepInEx 插件表失败：{ex.Message}");
                _log.LogError($"[静态扫描] 读取插件表异常：{ex}");
                return report;
            }

            report.AllPlugins.AddRange(plugins);
            _log.LogInfo($"[静态扫描] 检测到 {plugins.Count} 个已加载插件。");

            foreach (var plugin in plugins)
            {
                try
                {
                    ClassifyPlugin(plugin, trustedGuids, trustedNames, report);
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"分析插件 {plugin.Name} 失败：{ex.Message}");
                    _log.LogWarning($"[静态扫描] 分析插件 {plugin} 时异常：{ex.Message}");
                }
            }

            if (report.Violations.Count == 0)
                _log.LogInfo("[静态扫描] 未发现已知作弊插件。");
            else
                _log.LogWarning($"[静态扫描] 发现 {report.Violations.Count} 项作弊软件证据！");

            return report;
        }

        /// <summary>从 BepInEx 6 的 IL2CPP 插件链读取已加载插件表。</summary>
        private List<LoadedPluginInfo> CollectLoadedPlugins()
        {
            var result = new List<LoadedPluginInfo>();

            var chainloader = IL2CPPChainloader.Instance;
            if (chainloader?.Plugins == null) return result;

            foreach (var kv in chainloader.Plugins)
            {
                var info = kv.Value;
                if (info == null) continue;

                var meta = info.Metadata;
                var entry = new LoadedPluginInfo
                {
                    Guid = meta?.GUID ?? kv.Key ?? string.Empty,
                    Name = meta?.Name ?? string.Empty,
                    Version = meta?.Version?.ToString() ?? string.Empty,
                    Location = info.Location ?? string.Empty,
                    EntryType = info.TypeName ?? string.Empty,
                };

                // 插件名缺失时退回程序集文件名，避免出现空名字条目
                if (string.IsNullOrEmpty(entry.Name) && !string.IsNullOrEmpty(entry.Location))
                    entry.Name = Path.GetFileNameWithoutExtension(entry.Location);

                result.Add(entry);
            }

            return result;
        }

        /// <summary>对单个插件做三级分类：受信任 / 灰色 / 黑名单。</summary>
        private void ClassifyPlugin(
            LoadedPluginInfo plugin,
            string[] trustedGuids,
            string[] trustedNames,
            ScanReport report)
        {
            var display = plugin.ToString();

            // ---------- 第一优先：用户显式白名单 ----------
            if (MatchesAny(plugin.Guid, trustedGuids) ||
                MatchesAny(plugin.Name, trustedNames) ||
                MatchesAny(plugin.Location, trustedNames))
            {
                report.TrustedPlugins.Add($"{display}（用户白名单）");
                return;
            }

            // ---------- 第二优先：内置默认受信任（玩法模组 / 工具模组） ----------
            if (CheatSignatureDb.IsDefaultTrusted(plugin.Name) ||
                CheatSignatureDb.IsDefaultTrusted(plugin.Guid) ||
                CheatSignatureDb.IsDefaultTrusted(plugin.Location))
            {
                report.TrustedPlugins.Add($"{display}（内置信任：玩法/工具模组）");
                return;
            }

            // ---------- 黑名单匹配 ----------
            var hit = MatchBlacklist(plugin);
            if (hit != null)
            {
                report.Violations.Add(BuildViolation(plugin, hit, "元数据匹配"));
                return;
            }

            // ---------- 灰名单匹配（仅记录） ----------
            var gray = MatchGraylist(plugin);
            if (gray != null)
            {
                report.GrayPlugins.Add($"{display} -> {gray.Name}（{gray.Note}）");
                report.Violations.Add(new Violation(
                    ViolationKind.UnknownPlugin,
                    Severity.Low,
                    -1,
                    plugin.Name,
                    UnityEngine.Time.time,
                    $"灰名单模组：{gray.Name} — {gray.Note}",
                    new Dictionary<string, float>()));
                return;
            }

            // ---------- 未收录插件：仅登记，不判定 ----------
            report.GrayPlugins.Add($"{display}（未收录，仅登记）");
            if (_cfg.VerboseLogging.Value)
                _log.LogInfo($"[静态扫描] 未收录插件：{display}");
        }

        private CheatSignature MatchBlacklist(LoadedPluginInfo plugin)
        {
            foreach (var sig in CheatSignatureDb.Blacklist)
            {
                // 1) GUID 精确匹配
                if (MatchesAnyExact(plugin.Guid, sig.Guids))
                    return sig;

                // 2) 插件名 / 程序集名片段
                if (MatchesAny(plugin.Name, sig.NameMarkers) ||
                    MatchesAny(plugin.Location, sig.NameMarkers) ||
                    MatchesAny(plugin.Guid, sig.Guids))
                    return sig;

                // 3) 需要读文件才能做的深度匹配
                if (!string.IsNullOrEmpty(plugin.Location) && File.Exists(plugin.Location))
                {
                    if (FileContainsAny(plugin.Location, sig.TypeMarkers) ||
                        FileContainsAny(plugin.Location, sig.StringMarkers))
                        return sig;
                }
            }
            return null;
        }

        private CheatSignature MatchGraylist(LoadedPluginInfo plugin)
        {
            foreach (var sig in CheatSignatureDb.Graylist)
            {
                if (MatchesAny(plugin.Name, sig.NameMarkers) ||
                    MatchesAny(plugin.Location, sig.NameMarkers) ||
                    MatchesAny(plugin.Guid, sig.Guids))
                    return sig;

                if (!string.IsNullOrEmpty(plugin.Location) && File.Exists(plugin.Location))
                {
                    if (FileContainsAny(plugin.Location, sig.TypeMarkers))
                        return sig;
                }
            }
            return null;
        }

        private Violation BuildViolation(LoadedPluginInfo plugin, CheatSignature sig, string how)
        {
            _log.LogWarning($"[静态扫描] 命中作弊特征：{sig.Name} <- {plugin}（{how}）");

            return new Violation(
                ViolationKind.KnownCheatPlugin,
                sig.Severity,
                -1,                       // 静态证据不归属某个游戏内玩家
                plugin.Name,
                UnityEngine.Time.time,
                $"检测到作弊软件「{sig.Name}」（类别：{sig.Category}，匹配方式：{how}）。{sig.Note}",
                new Dictionary<string, float>
                {
                    ["signature_index"] = CheatSignatureDb.Blacklist.IndexOf(sig),
                });
        }

        // ================= 匹配辅助 =================

        /// <summary>精确匹配（用于 GUID）。</summary>
        private static bool MatchesAnyExact(string value, string[] candidates)
        {
            if (string.IsNullOrEmpty(value) || candidates == null) return false;
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (string.Equals(value, c, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>子串匹配（不区分大小写）。</summary>
        private static bool MatchesAny(string value, string[] candidates)
        {
            if (string.IsNullOrEmpty(value) || candidates == null) return false;
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (value.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 扫描程序集文件，检查是否包含任意标记。
        /// 同时覆盖 ASCII 与 UTF-16LE 两种字符串存储方式——
        /// .NET 元数据里的类型名是 UTF-8，而代码里的字面量可能是 UTF-16。
        /// </summary>
        private static bool FileContainsAny(string path, string[] markers)
        {
            if (markers == null || markers.Length == 0) return false;

            byte[] bytes;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var length = (int)Math.Min(fs.Length, MaxScanBytes);
                bytes = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = fs.Read(bytes, read, length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < length) Array.Resize(ref bytes, read);
            }
            catch
            {
                // 文件被占用或权限不足：放弃深度匹配，退回元数据匹配结果
                return false;
            }

            // ASCII / UTF-8 视角
            var ascii = Encoding.UTF8.GetString(bytes);
            // UTF-16LE 视角：把低字节抽出来，等价于逐字符比较
            var utf16 = ExtractUtf16LowBytes(bytes, ascii.Length);

            foreach (var marker in markers)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                if (ascii.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (utf16.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>从字节流中抽取 UTF-16LE 的低字节序列，用于识别宽字符字面量。</summary>
        private static string ExtractUtf16LowBytes(byte[] bytes, int capacity)
        {
            var sb = new StringBuilder(Math.Min(capacity, bytes.Length / 2 + 1));
            for (var i = 0; i + 1 < bytes.Length; i += 2)
            {
                var lo = bytes[i];
                var hi = bytes[i + 1];
                // 只保留「可打印 ASCII + 高字节为 0」的位置，其余用占位符断开，
                // 避免相邻无关字节拼接出假匹配。
                sb.Append(hi == 0 && lo >= 0x20 && lo < 0x7F ? (char)lo : '\u0000');
            }
            return sb.ToString();
        }
    }
}
