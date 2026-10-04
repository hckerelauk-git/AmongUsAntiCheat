using System;
using System.Collections.Generic;
using Hazel;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 识别房间里谁装了 Amethyst（插件 GUID <c>amethyst.mod</c>）。
    ///
    /// ────────────── 协议来源 ──────────────
    ///
    /// Amethyst 自己有一套「模组客户端探测」（反编译 AmethystModClientDetection）：
    /// 它挂在 <c>PlayerPhysics.HandleRpc</c> 上，用 callId <b>50</b>，
    /// payload 依次是：
    ///
    ///     WriteString("AMETHYST_MOD_CLIENT_V1")   // 固定标识
    ///     WriteString("P" 或 "R")                 // P=探测，R=应答
    ///     WriteByte(playerId)                     // 发送者自己的玩家号
    ///     WriteString(version)                    // 版本号
    ///
    /// 进对局后每 8 秒广播一次探测；别人收到探测会回一条应答。
    /// 所以 **只要被动监听，8 秒内就能发现房间里的 Amethyst 用户**。
    ///
    /// ────────────── 两条必须守住的边界 ──────────────
    ///
    /// 1. **绝不发送探测包。**
    ///    发出去的话，对方的 Amethyst 会把我们登记成「Amethyst 用户」——
    ///    那是冒名。ACE 只读不写。
    ///
    /// 2. **绝不丢包（Prefix 永远返回 true）。**
    ///    Amethyst 识别到自己的包后会 return false 把包吃掉。
    ///    我们若也返回 false，会破坏它自己的互认。
    ///
    /// 另外读完之后必须把 <c>reader.Position</c> 复原：我们只是「旁听」，
    /// 真正的解析权还在 Amethyst 和游戏手里，读坏了会让它们解析错位。
    /// </summary>
    internal static class AmethystPresence
    {
        /// <summary>Amethyst 用的 RPC callId（挂在 PlayerPhysics.HandleRpc 上）。</summary>
        public const byte AmethystCallId = 50;

        /// <summary>Amethyst 的固定标识串。</summary>
        private const string Marker = "AMETHYST_MOD_CLIENT_V1";

        /// <summary>Amethyst 的 BepInEx 插件 GUID。它的互认包不带 GUID，这是反编译确认后填的。</summary>
        internal const string AmethystGuid = "amethyst.mod";

        /// <summary>默认标记文案。常量集中在 <see cref="PresenceTags"/>。</summary>
        public const string DefaultTag = PresenceTags.AmethystDefault;

        private static readonly HashSet<int> AmethystPlayers = new HashSet<int>(16);
        private static readonly Dictionary<int, string> Versions = new Dictionary<int, string>(16);

        public static int AmethystUserCount => AmethystPlayers.Count;

        public static bool IsAmethystUser(int playerId) => AmethystPlayers.Contains(playerId);

        public static void Reset()
        {
            AmethystPlayers.Clear();
            Versions.Clear();
        }

        /// <summary>
        /// 旁听一条 <c>PlayerPhysics.HandleRpc</c>。
        /// 识别到 Amethyst 包时登记发送者；无论结果如何都复原 reader 位置。
        /// </summary>
        /// <returns>是否确认是 Amethyst 的包（调用方不应据此丢包）。</returns>
        public static bool TryAccept(byte callId, MessageReader reader, PlayerControl sender)
        {
            if (callId != AmethystCallId || reader == null || sender == null) return false;

            var start = -1;
            try { start = reader.Position; }
            catch { return false; }

            try
            {
                if (reader.ReadString() != Marker) return false;

                reader.ReadString();              // "P" 探测 / "R" 应答
                var claimedId = reader.ReadByte();
                var version = reader.ReadString();

                // 只认「包里声明的玩家号 == 发送者自己的号」的包。
                // 否则任何人都能伪造一条带着别人 id 的包来栽赃。
                if (claimedId != sender.PlayerId) return true;

                // 忽略自己：部分版本会把本机广播回送一遍。
                var local = GameBridge.GetLocalPlayer();
                if (local != null && local.PlayerId == claimedId) return true;

                // 登记到模组指纹库：它自报了身份，直接登记。
                // 注意它的互认包本身**不带 GUID**（反编译确认），GUID 是我们按已知事实填的。
                ModFingerprint.Announce(claimedId, AmethystGuid);

                if (AmethystPlayers.Add(claimedId))
                {
                    Versions[claimedId] = version;
                    AntiCheatRuntime.Log?.LogInfo(
                        $"[AME 互认] 发现 Amethyst 用户：{GameBridge.GetPlayerName(sender)}" +
                        $"（id={claimedId}，版本 {version}）");
                }
                else
                {
                    Versions[claimedId] = version;
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                // 关键：旁听者必须把读取位置还回去，让 Amethyst 与游戏按原样解析。
                try { reader.Position = start; } catch { }
            }
        }

        /// <summary>取某个玩家上报的 Amethyst 版本；未知返回 null。</summary>
        public static string GetVersion(int playerId) =>
            Versions.TryGetValue(playerId, out var v) ? v : null;
    }
}
