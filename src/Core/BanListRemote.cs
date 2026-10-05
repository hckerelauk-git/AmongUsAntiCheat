using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 在线封禁名单。
    ///
    /// 从 ACE 的公开端点拉取名单，和内置名单、本地自定义名单合并使用。
    ///
    /// 三条硬约束：
    ///
    /// **1. 绝不阻塞游戏线程。**
    /// 网络请求全在后台任务里跑，游戏线程只读一份已经解析好的只读快照。
    /// 网络读取期间不得造成掉帧 —— 该功能属辅助查询，
    /// 不能因为它让整局游戏卡顿。
    ///
    /// **2. 拉不到就用旧的。**
    /// 启动先读本地缓存，再后台刷新。断网、端点挂了、返回乱码，
    /// 均不影响已有名单生效 —— 名单为可选增强，非必要依赖。
    ///
    /// **3. 不信任远端内容。**
    /// 字段做长度截断、条目数设上限、解析失败整批丢弃。
    /// 远端数据一旦异常，最坏结果是这次名单不更新，绝不能把内存或界面撑爆。
    /// </summary>
    internal static class BanListRemote
    {
        /// <summary>默认端点。可以在配置里改。</summary>
        public const string DefaultEndpoint = "https://api2.elauk.top/acban";

        /// <summary>
        /// 日志钩子。
        ///
        /// 核心层不直接引用运行时 —— 那会把整个运行时（含 Unity / 游戏程序集依赖）
        /// 拖进单元测试工程。由运行时在初始化时把日志方法装进来；
        /// 没装就静默，不影响功能。
        /// </summary>
        public static Action<string> LogInfo;

        /// <summary>警告级日志钩子，同上。</summary>
        public static Action<string> LogWarning;

        /// <summary>条目数上限。远端异常时防止把内存吃光。</summary>
        private const int MaxEntries = 5000;

        /// <summary>单个字段的长度上限（名字 / 理由等）。</summary>
        private const int MaxFieldLength = 200;

        private const int TimeoutSeconds = 15;

        /// <summary>
        /// 当前生效的远端条目。
        ///
        /// **整体替换引用**而不是原地修改 —— 读方（游戏线程）拿到的永远是
        /// 一份完整快照，不会读到「改了一半」的列表，也就不需要加锁。
        /// </summary>
        private static volatile List<BanEntry> _entries = new List<BanEntry>();

        public static IReadOnlyList<BanEntry> Entries => _entries;

        /// <summary>远端数据的更新时间（Unix 秒），0 表示还没成功过。</summary>
        public static long LastUpdatedUnix { get; private set; }

        /// <summary>最近一次失败原因，供界面/日志展示；成功时清空。</summary>
        public static string LastError { get; private set; }

        private static bool _inFlight;
        private static float _nextRefresh;
        private static bool _cacheLoaded;

        /// <summary>当前条目数。</summary>
        public static int Count => _entries.Count;

        /// <summary>
        /// 由帧驱动调用。到点就起一个后台任务去刷新，立刻返回。
        /// </summary>
        /// <param name="now">当前时间（Time.unscaledTime）。</param>
        /// <param name="enabled">是否启用在线名单。</param>
        /// <param name="endpoint">端点地址。</param>
        /// <param name="refreshMinutes">刷新间隔（分钟）。</param>
        public static void Tick(float now, bool enabled, string endpoint, float refreshMinutes)
        {
            if (!_cacheLoaded)
            {
                _cacheLoaded = true;
                LoadCache();
            }

            if (!enabled) return;
            if (string.IsNullOrWhiteSpace(endpoint)) return;
            if (now < _nextRefresh) return;

            // 起过一次就不再叠加：网络慢的时候每秒起一个任务会把连接池打满。
            if (_inFlight) return;

            var minutes = Math.Max(1f, refreshMinutes);
            _nextRefresh = now + minutes * 60f;
            FetchAsync(endpoint);
        }

        /// <summary>强制立刻刷新一次（界面上「立即刷新」用）。</summary>
        public static void RefreshNow(string endpoint)
        {
            if (_inFlight || string.IsNullOrWhiteSpace(endpoint)) return;
            _nextRefresh = 0f;
            FetchAsync(endpoint);
        }

        private static void FetchAsync(string endpoint)
        {
            _inFlight = true;

            // 必须在后台线程：HttpClient 同步等待会把游戏主线程卡住，
            // 而这里的调用点就在每帧的检测循环里。
            _ = Task.Run(async () =>
            {
                try
                {
                    // **直连，不走系统代理。**
                    //
                    // 实测走代理会超时：本机配了 127.0.0.1:7890 一类的本地代理，
                    // HttpClient 默认会读取系统代理设置，而该端点在国内（腾讯云），
                    // 经代理绕一圈反而连不上 —— 769 字节的响应 10 秒超时。
                    // 这里显式关掉代理。
                    using var handler = new HttpClientHandler { UseProxy = false };
                    using var client = new HttpClient(handler)
                    {
                        Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
                    };
                    using var resp = await client.GetAsync(endpoint).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();

                    var raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (Parse(raw, out var parsed, out var updatedAt, out var error))
                    {
                        _entries = parsed;
                        LastUpdatedUnix = updatedAt;
                        LastError = null;
                        SaveCache(raw);

                        LogInfo?.Invoke("[在线名单] 已更新：" + parsed.Count + " 条"
                            + (updatedAt > 0 ? "（数据时间 " + FromUnix(updatedAt).ToString("yyyy-MM-dd HH:mm") + "）" : ""));
                    }
                    else
                    {
                        LastError = error;
                        LogWarning?.Invoke("[在线名单] 本次未更新：" + error);
                    }
                }
                catch (Exception ex)
                {
                    // 拉不到就继续用旧的 —— 这是「有就用」的功能，不是必需品。
                    LastError = ex.GetType().Name + "：" + ex.Message;
                    LogWarning?.Invoke("[在线名单] 请求失败，继续使用已有名单：" + LastError);
                }
                finally
                {
                    _inFlight = false;
                }
            });
        }

        /// <summary>
        /// 解析端点返回的 JSON。**纯函数，可脱离 Unity 直接测。**
        ///
        /// 期望格式：
        /// <code>
        /// { "updatedAt": 1791172311, "count": 3,
        ///   "bans": [ { "name": "...", "friendCode": "...", "puid": "...", "reason": "..." } ] }
        /// </code>
        ///
        /// 单条解析失败只跳过那一条；整体结构不对才整批丢弃 ——
        /// 一条脏数据不该让整份名单作废。
        /// </summary>
        public static bool Parse(string json, out List<BanEntry> entries, out long updatedAt, out string error)
        {
            entries = new List<BanEntry>();
            updatedAt = 0;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "返回内容为空";
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "顶层不是对象";
                    return false;
                }

                if (root.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.Number)
                    updatedAt = u.TryGetInt64(out var uv) ? uv : 0;

                if (!root.TryGetProperty("bans", out var bans) || bans.ValueKind != JsonValueKind.Array)
                {
                    error = "缺少 bans 数组";
                    return false;
                }

                var skipped = 0;
                foreach (var item in bans.EnumerateArray())
                {
                    if (entries.Count >= MaxEntries) break;
                    if (item.ValueKind != JsonValueKind.Object) { skipped++; continue; }

                    var name = Field(item, "name");
                    var code = Field(item, "friendCode");
                    var puid = Field(item, "puid");
                    var reason = Field(item, "reason");
                    var source = Field(item, "source");

                    // 没有任何可匹配的标识 —— 这种条目收了也没用，只会白占内存。
                    if (code.Length == 0 && puid.Length == 0)
                    {
                        skipped++;
                        continue;
                    }

                    entries.Add(new BanEntry
                    {
                        Name = name.Length > 0 ? name : (code.Length > 0 ? code : puid),
                        Code = code,
                        Puid = puid,
                        Reason = reason.Length > 0
                            ? reason + (source.Length > 0 ? "（来源 " + source + "）" : string.Empty)
                            : "在线封禁名单",
                    });
                }

                if (entries.Count == 0 && skipped > 0)
                {
                    error = "全部 " + skipped + " 条都缺少可匹配的标识";
                    return false;
                }

                return true;
            }
            catch (JsonException ex)
            {
                error = "JSON 解析失败：" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        private static string Field(JsonElement item, string key)
        {
            try
            {
                if (!item.TryGetProperty(key, out var v)) return string.Empty;
                if (v.ValueKind != JsonValueKind.String) return string.Empty;

                var s = (v.GetString() ?? string.Empty).Trim();
                return s.Length > MaxFieldLength ? s.Substring(0, MaxFieldLength) : s;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 把一条封禁推送到在线名单。失败只记日志，不影响本地已生效的封禁。
        ///
        /// **默认不启用**：只有配置了写入令牌的机器才会推送。
        /// 普通用户点「封禁」只写本地 —— 这样共享名单不会被随意改动。
        ///
        /// **不要带 Origin 头**：服务端的同源校验是「有 Origin 才比对、没有就放行」，
        /// 带上 Origin 反而会被判为跨站请求而 403。
        /// 这个校验本意是防别的网站直接调我们的写入接口，插件不是浏览器，不适用。
        /// </summary>
        public static void PushBan(string endpoint, string token, BanEntry entry)
        {
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token)) return;
            if (entry == null) return;
            if (entry.Code.Length == 0 && entry.Puid.Length == 0) return;

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                name = entry.Name,
                friendCode = entry.Code,
                puid = entry.Puid,
                reason = entry.Reason,
                source = "ACE",
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    using var handler = new HttpClientHandler { UseProxy = false };
                    using var client = new HttpClient(handler)
                    {
                        Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
                    };

                    using var req = new HttpRequestMessage(HttpMethod.Post, PushUrlOf(endpoint))
                    {
                        Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
                    };
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                    using var resp = await client.SendAsync(req).ConfigureAwait(false);
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    var ok = (int)resp.StatusCode == 200 || (int)resp.StatusCode == 409;   // 409 = 已在名单里，等同成功
                    LogInfo?.Invoke("[在线名单] 推送封禁" + (ok ? "成功" : "失败（HTTP " + (int)resp.StatusCode + "）")
                        + "：" + (body.Length > 120 ? body.Substring(0, 120) : body));
                }
                catch (Exception ex)
                {
                    LogWarning?.Invoke("[在线名单] 推送封禁失败（本地仍生效）：" + ex.Message);
                }
            });
        }

        /// <summary>由名单端点推出写入地址（/acban → /acban/api/ban）。</summary>
        private static string PushUrlOf(string endpoint)
        {
            var trimmed = endpoint.TrimEnd('/');
            return trimmed + "/api/ban";
        }

        // ================= 本地缓存 =================
        //
        // 缓存的意义：游戏刚启动、网络还没回来的那几秒里，
        // 名单不能是空的 —— 否则刚好在启动瞬间进房的人就漏过去了。

        private static string CachePath
        {
            get
            {
                try
                {
                    var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "ACELogs");
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "banlist-cache.json");
                }
                catch { return null; }
            }
        }

        private static void LoadCache()
        {
            try
            {
                var path = CachePath;
                if (path == null || !File.Exists(path)) return;

                var raw = File.ReadAllText(path);
                if (Parse(raw, out var parsed, out var updatedAt, out _))
                {
                    _entries = parsed;
                    LastUpdatedUnix = updatedAt;
                    LogInfo?.Invoke("[在线名单] 已从本地缓存载入 " + parsed.Count + " 条。");
                }
            }
            catch (Exception ex)
            {
                LogWarning?.Invoke("[在线名单] 读取缓存失败（忽略）：" + ex.Message);
            }
        }

        private static void SaveCache(string raw)
        {
            try
            {
                var path = CachePath;
                if (path != null) File.WriteAllText(path, raw);
            }
            catch { /* 缓存写不进去不影响本次生效 */ }
        }

        private static DateTime FromUnix(long unix)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime; }
            catch { return DateTime.MinValue; }
        }
    }
}
