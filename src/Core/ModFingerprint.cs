using System.Collections.Generic;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 按 RPC 指纹识别每个玩家在用什么模组。
    ///
    /// **被动观察**：只要对方发过一次自定义 RPC，就认出来了 ——
    /// 不需要对方配合，作弊端也躲不掉（除非它一个自定义包都不发，那等于没装）。
    ///
    /// 观测点在 `PlayerControl.HandleRpc` 的 Prefix（见 RpcContextPatch），
    /// 那里能看到**每一个** RPC 及其发送者。
    ///
    /// 识别分两级：
    ///   1. **callId 超出原版范围（&gt; 67）** → 一定是模组，查表得名字；
    ///      查不到就按原始 ID 记为「未知模组 #N」，方便日后补库
    ///   2. **自报家门的模组**（Amethyst / ACE）→ 有 GUID，查 GUID 表得名字；
    ///      查不到就**直接显示 GUID 原文**，不显示空白
    /// </summary>
    internal static class ModFingerprint
    {
        /// <summary>单个玩家被识别出的模组。</summary>
        internal sealed class Detected
        {
            /// <summary>显示名（可能是模组名，也可能是 GUID 原文或「未知模组 #N」）。</summary>
            public string Display;

            /// <summary>已知的 GUID；只有自报家门的模组才有。</summary>
            public string Guid;

            /// <summary>是否已知作弊端。</summary>
            public bool IsCheat;
        }

        private static readonly Dictionary<int, Dictionary<string, Detected>> ByPlayer =
            new Dictionary<int, Dictionary<string, Detected>>(16);

        /// <summary>自报家门时登记的 GUID（Amethyst / ACE 互认用）。</summary>
        private static readonly Dictionary<int, string> AnnouncedGuid = new Dictionary<int, string>(16);

        public static void Reset()
        {
            ByPlayer.Clear();
            AnnouncedGuid.Clear();
        }

        /// <summary>某个玩家识别出的全部模组（按显示名去重）。</summary>
        public static ICollection<Detected> Of(int playerId)
        {
            return ByPlayer.TryGetValue(playerId, out var map) ? map.Values : System.Array.Empty<Detected>();
        }

        public static int CountOf(int playerId) =>
            ByPlayer.TryGetValue(playerId, out var map) ? map.Count : 0;

        /// <summary>
        /// 记录一次观测到的 RPC。
        ///
        /// 由 <c>PlayerControl.HandleRpc</c> 的 Prefix 调用 —— 热路径，
        /// 所以原版范围内的 callId 要尽快返回，不做任何额外工作。
        /// </summary>
        public static void Observe(PlayerControl sender, int callId)
        {
            if (sender == null || callId < 0 || callId > 255) return;

            // 原版范围内的直接放行 —— 这是绝大多数包，不能在这里做任何分配。
            if (!ModFingerprintDb.IsCustomCallId(callId)) return;

            var id = GameBridge.GetPlayerId(sender);
            if (id < 0) return;

            if (ModFingerprintDb.ByCallId.TryGetValue((byte)callId, out var entry))
            {
                Add(id, entry.Name, entry.Guid, entry.IsCheat);
                return;
            }

            // 认不出的自定义 ID：按原始编号记下来。
            // 显示成「未知模组 #164」这种，用户看到就知道该往库里补什么。
            Add(id, "未知模组 #" + callId, null, false);
        }

        /// <summary>
        /// 模组自报家门时登记它的 GUID（Amethyst / ACE 互认包）。
        ///
        /// 库中可查到则显示名称，查不到则显示 GUID 原文 —— 用户明确要求的行为。
        /// </summary>
        public static void Announce(int playerId, string guid)
        {
            if (playerId < 0 || string.IsNullOrEmpty(guid)) return;
            AnnouncedGuid[playerId] = guid;

            var display = ModFingerprintDb.DisplayOf(guid);
            var isCheat = IsKnownCheatGuid(guid);
            Add(playerId, display, guid, isCheat);
        }

        private static bool IsKnownCheatGuid(string guid)
        {
            foreach (var sig in CheatSignatureDb.Blacklist)
            {
                if (sig.Guids == null) continue;
                for (var i = 0; i < sig.Guids.Length; i++)
                    if (string.Equals(sig.Guids[i], guid, System.StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }

        private static void Add(int playerId, string display, string guid, bool isCheat)
        {
            if (string.IsNullOrEmpty(display)) return;

            if (!ByPlayer.TryGetValue(playerId, out var map))
            {
                map = new Dictionary<string, Detected>(4);
                ByPlayer[playerId] = map;
            }

            if (map.ContainsKey(display)) return;

            map[display] = new Detected { Display = display, Guid = guid, IsCheat = isCheat };
        }
    }
}
