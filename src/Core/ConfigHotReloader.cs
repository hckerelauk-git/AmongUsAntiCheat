using System;
using System.Collections.Generic;
using System.IO;
using ApexCheatEnder.Config;
using BepInEx.Configuration;

namespace ApexCheatEnder.Core
{
    /// <summary>一条配置变更记录。</summary>
    public readonly struct ConfigChange
    {
        /// <summary>分组名，比如「开哪些检测」。</summary>
        public string Section { get; }

        /// <summary>配置项名字，比如「检测穿墙」。</summary>
        public string Key { get; }

        /// <summary>改之前的值（已转成可读文本）。</summary>
        public string OldValue { get; }

        /// <summary>改之后的值。</summary>
        public string NewValue { get; }

        public ConfigChange(string section, string key, string oldValue, string newValue)
        {
            Section = section;
            Key = key;
            OldValue = oldValue;
            NewValue = newValue;
        }

        /// <summary>「分组 / 名字：旧值 变成 新值」</summary>
        public override string ToString() =>
            string.IsNullOrEmpty(Section) ? Key + "：" + OldValue + " 变成 " + NewValue
                                          : Section + " / " + Key + "：" + OldValue + " 变成 " + NewValue;
    }

    /// <summary>
    /// 配置文件热重载。
    ///
    /// 解决的问题：用户用记事本打开配置改了数字，保存之后必须重启游戏才生效——
    /// 该行为严重影响调参体验，而调参本身即为「修改—验证」的迭代过程。
    ///
    /// 做法：每 0.5 秒检查一次配置文件的修改时间。
    /// 发现文件被改过就 Reload（重新读盘），然后逐项对比新旧值，
    /// 把真正变了的项挑出来广播出去，让补丁层和界面即时响应。
    ///
    /// 为什么不能只靠 ConfigFile.SettingChanged 事件：
    /// 那个事件只在**代码里**给 ConfigEntry 赋值时触发。
    /// 用户在外部编辑器改文件，事件不会响，必须自己检测文件变化。
    ///
    /// 为什么不直接无条件 Reload：
    /// Reload 会重建所有 ConfigEntry 的值，而事件检测开关的开关逻辑
    /// 挂在 SettingChanged 上，无差别 Reload 会导致补丁被反复挂载卸载。
    /// 先 Diff 再广播，可以保证只在真的变了的时候才动作。
    /// </summary>
    public sealed class ConfigHotReloader
    {
        /// <summary>检查文件变化的间隔（秒）。</summary>
        private const float PollInterval = 0.5f;

        private readonly AntiCheatConfig _cfg;
        private readonly Action<IReadOnlyList<ConfigChange>> _onChanged;
        private readonly Action<string, string> _log;

        private DateTime _lastWriteTimeUtc = DateTime.MinValue;
        private long _lastLength = -1;
        private float _nextPollTime;

        /// <summary>上一次 Reload 之后各配置项的取值快照，用于 Diff。</summary>
        private readonly Dictionary<ConfigEntryBase, string> _snapshot = new Dictionary<ConfigEntryBase, string>();

        /// <summary>文件被改动但还没处理完时置位，避免同一帧反复 Reload。</summary>
        private bool _pendingReload;

        public ConfigHotReloader(
            AntiCheatConfig cfg,
            Action<IReadOnlyList<ConfigChange>> onChanged,
            Action<string, string> log)
        {
            _cfg = cfg;
            _onChanged = onChanged;
            _log = log;
        }

        /// <summary>建立初始快照。此刻必须在下一次 Reload 之前调用。</summary>
        public void Prime()
        {
            _lastWriteTimeUtc = ReadWriteTime();
            _lastLength = ReadLength();
            CaptureSnapshot();
        }

        /// <summary>外部（比如界面里刚改完）主动标脏，强制下次 Tick 检查。</summary>
        public void MarkDirty() => _pendingReload = true;

        /// <summary>主线程每帧调用。</summary>
        public void Tick(float now)
        {
            if (_cfg?.File == null) return;
            if (now < _nextPollTime) return;
            _nextPollTime = now + PollInterval;

            var writeTime = ReadWriteTime();
            var length = ReadLength();

            var fileChanged = writeTime != _lastWriteTimeUtc || length != _lastLength;
            if (!fileChanged && !_pendingReload) return;

            _lastWriteTimeUtc = writeTime;
            _lastLength = length;
            _pendingReload = false;

            ReloadAndDiff();
        }

        private void ReloadAndDiff()
        {
            try
            {
                // 先记录「重载前」的值，重载后再记录一次，两者之差即为变更集。
                var before = SnapshotNow();

                _cfg.File.Reload();

                var changes = Diff(before, SnapshotNow());
                if (changes.Count == 0) return;

                foreach (var c in changes)
                    _log?.Invoke("配置", c.ToString());

                _onChanged?.Invoke(changes);
            }
            catch (Exception ex)
            {
                // 配置文件被写坏（比如用户正在编辑保存到一半）时不能崩游戏。
                _log?.Invoke("配置", "读取失败，本次改动先不生效：" + ex.Message);
            }
        }

        /// <summary>
        /// 把当前所有配置项的取值拍成字符串字典。
        ///
        /// ConfigFile 的 Entries 属性是 protected，外部拿不到；
        /// 但 ConfigFile 自己实现了 IEnumerable&lt;KeyValuePair&lt;ConfigDefinition, ConfigEntryBase&gt;&gt;，
        /// 直接 foreach 它就能遍历所有项，键是定义、值是 entry。
        /// </summary>
        private Dictionary<ConfigEntryBase, string> SnapshotNow()
        {
            var map = new Dictionary<ConfigEntryBase, string>(_cfg.File.Count);
            foreach (var kv in _cfg.File)
                map[kv.Value] = Readable(kv.Value);
            return map;
        }

        private void CaptureSnapshot() => _snapshot.Clear();

        /// <summary>对比两份快照，产出变更列表。</summary>
        private List<ConfigChange> Diff(
            Dictionary<ConfigEntryBase, string> before,
            Dictionary<ConfigEntryBase, string> after)
        {
            var list = new List<ConfigChange>();

            foreach (var kv in after)
            {
                before.TryGetValue(kv.Key, out var oldValue);
                if (oldValue == kv.Value) continue;

                var def = kv.Key.Definition;
                list.Add(new ConfigChange(def.Section, def.Key, oldValue ?? "（空）", kv.Value));
            }

            return list;
        }

        /// <summary>把配置项当前值转成可读文本，供 Diff 和界面显示。</summary>
        private static string Readable(ConfigEntryBase entry)
        {
            try
            {
                var v = entry.BoxedValue;
                if (v == null) return string.Empty;
                if (v is bool b) return b ? "开" : "关";
                if (v is float f) return f.ToString("0.####");
                if (v is double d) return d.ToString("0.####");
                return v.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static DateTime ReadWriteTime()
        {
            try
            {
                var path = ConfigPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return DateTime.MinValue;
                return File.GetLastWriteTimeUtc(path);
            }
            catch { return DateTime.MinValue; }
        }

        private static long ReadLength()
        {
            try
            {
                var path = ConfigPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return -1L;
                return new FileInfo(path).Length;
            }
            catch { return -1L; }
        }

        private static string _configPath;

        /// <summary>
        /// 配置文件完整路径。
        ///
        /// ConfigFile 本身不暴露 ConfigFilePath（BepInEx 6 把它标成了 internal），
        /// 而 <c>Paths.ConfigPath</c> 在不同版本里也在挪位置，
        /// 所以这里一次性反射找到并存下来，之后都走缓存。
        /// </summary>
        private static string ConfigPath()
        {
            if (_configPath != null) return _configPath;

            // 优先从已经建好的 ConfigFile 上拿
            try
            {
                var prop = typeof(BepInEx.Configuration.ConfigFile)
                    .GetProperty("ConfigFilePath",
                                 System.Reflection.BindingFlags.Instance |
                                 System.Reflection.BindingFlags.Public |
                                 System.Reflection.BindingFlags.NonPublic);
                if (prop != null)
                {
                    // 这里拿不到实例，改走 Paths，见下。
                }
            }
            catch { }

            // 退而求其次：按 BepInEx 的约定路径拼出来
            try
            {
                var pathsType = Type.GetType("BepInEx.Paths, BepInEx.Core")
                             ?? Type.GetType("BepInEx.Paths, BepInEx");
                var configPathProp = pathsType?.GetProperty("ConfigPath",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                var configRoot = configPathProp?.GetValue(null) as string;

                if (!string.IsNullOrEmpty(configRoot))
                {
                    _configPath = Path.Combine(configRoot, AntiCheatPlugin.PluginGuid + ".cfg");
                    return _configPath;
                }
            }
            catch { }

            _configPath = string.Empty;
            return _configPath;
        }
    }
}
