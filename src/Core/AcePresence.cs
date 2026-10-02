using System;
using System.Collections.Generic;
using Hazel;
using UnityEngine;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// ACE 客户端互认（Presence）：识别房间里谁跟我一样装了 Apex Cheat Ender。
    ///
    /// 做法：占用一个游戏用不到的 RPC callId（<see cref="MagicCallId"/>），
    /// 周期性向房间里每个人广播一小段固定字节；谁的客户端也装了这个插件，
    /// 谁就会回同样的字节。收到即登记为「同装 ACE」。
    ///
    /// 为什么 payload 里要带魔法串、而不只靠 callId 判定：
    ///   callId 只有 1 字节，将来游戏版本可能启用 0xF0。只认 callId 的话，
    ///   一条正常的游戏 RPC 就会被当成握手包，把普通人误标成 ACE 用户。
    ///   带上 4 字节内容做校验，误判概率可以忽略。
    ///
    /// 安全边界：本模块只发一种固定长度的包，不接收也不执行任何指令，
    /// 收到的内容除了比对魔法串之外不做别的用途 —— 对方无法借此控制本机。
    /// </summary>
    internal static class AcePresence
    {
        /// <summary>
        /// 自定义 RPC 的 callId。
        /// 游戏的 RpcCalls 枚举只用到 0x00~0x40 一带，取 0xF0 避开。
        /// </summary>
        public const byte MagicCallId = 0xF0;

        /// <summary>握手包的标识（"ACE1"）。末位留给将来协议升级。</summary>
        private static readonly byte[] Magic = { 0x41, 0x43, 0x45, 0x31 };

        /// <summary>广播间隔（秒）。太密没必要，太疏新加入的玩家要等很久才被发现。</summary>
        private const float BroadcastInterval = 8f;

        /// <summary>名字标记的刷新间隔（秒）。游戏会自己重写名字文本，需要持续纠正。</summary>
        private const float NameRefreshInterval = 0.15f;

        private static readonly HashSet<int> AcePlayers = new HashSet<int>(16);

        private static float _nextBroadcast;
        private static float _nextNameRefresh;
        private static bool _sendFailureLogged;

        /// <summary>已确认同装 ACE 的玩家数。</summary>
        public static int AceUserCount => AcePlayers.Count;

        public static bool IsAceUser(int playerId) => AcePlayers.Contains(playerId);

        /// <summary>离开对局时清空：PlayerId 在每局会重新分配，留着会张冠李戴。</summary>
        public static void Reset()
        {
            AcePlayers.Clear();
            _nextBroadcast = 0f;
            _nextNameRefresh = 0f;
        }

        /// <summary>
        /// 收到一条 RPC 时调用。
        /// 命中则登记发送者并返回 true（调用方应拦下这条 RPC，不让游戏去处理未知 callId）。
        /// </summary>
        public static bool TryAccept(byte callId, MessageReader reader, PlayerControl sender)
        {
            if (callId != MagicCallId || sender == null || reader == null) return false;

            try
            {
                for (var i = 0; i < Magic.Length; i++)
                {
                    if (reader.ReadByte() != Magic[i]) return false;
                }
            }
            catch
            {
                // reader 长度不足或已被消费：不是我们的包，交回给游戏
                return false;
            }

            var id = GameBridge.GetPlayerId(sender);
            if (id < 0) return false;

            // 忽略自己：部分版本会把本机的广播回送一遍，
            // 登记自己会导致给自己头顶也挂上标记。
            var local = GameBridge.GetLocalPlayer();
            if (local != null && GameBridge.GetPlayerId(local) == id) return true;

            if (AcePlayers.Add(id))
            {
                AntiCheatRuntime.Log?.LogInfo(
                    $"[ACE 互认] 发现同装 ACE 的玩家：{GameBridge.GetPlayerName(sender)}（id={id}）");
            }

            return true;
        }

        /// <summary>每帧调用。负责周期性广播。</summary>
        public static void Tick()
        {
            if (!(AntiCheatRuntime.Config?.AcePresenceEnabled.Value ?? true)) return;

            // 不在对局里就清空：大厅/主菜单的 PlayerId 与对局中含义不同
            if (!GameBridge.IsInGame)
            {
                if (AcePlayers.Count > 0) AcePlayers.Clear();
                return;
            }

            var now = Time.time;
            if (now < _nextBroadcast) return;
            _nextBroadcast = now + BroadcastInterval;

            Broadcast();
        }

        /// <summary>每帧调用（由 PlayerControl.Update 的 Postfix 驱动）。</summary>
        public static void ApplyTag(PlayerControl player)
        {
            if (player == null || AcePlayers.Count == 0) return;

            var id = GameBridge.GetPlayerId(player);
            if (id < 0 || !AcePlayers.Contains(id)) return;

            // 节流：名字文本没必要每帧比对
            var now = Time.time;
            if (now < _nextNameRefresh) return;
            _nextNameRefresh = now + NameRefreshInterval;

            try
            {
                // 新版 Among Us 把头顶名字放在 cosmetics 层上，
                // PlayerControl 本身已经没有 nameText 字段了。
                var cosmetics = player.cosmetics;
                if (cosmetics == null) return;

                var nameText = cosmetics.nameText;
                if (nameText == null) return;

                var tag = AntiCheatRuntime.Config?.AcePresenceTag.Value;
                if (string.IsNullOrEmpty(tag)) tag = DefaultTag;

                var want = GameBridge.GetPlayerName(player) + " " + tag;
                if (nameText.text != want) nameText.text = want;
            }
            catch
            {
                // 名字文本结构随版本变化，拿不到就静默跳过，不影响反作弊主流程
            }
        }

        /// <summary>默认标记文案。两侧的 emoji 是用户指定样式。</summary>
        public const string DefaultTag = "😱ACE用户😱";

        private static void Broadcast()
        {
            var client = AmongUsClient.Instance;
            if (client == null) return;

            bool started;
            try { started = client.IsGameStarted; }
            catch { return; }
            if (!started) return;

            var local = GameBridge.GetLocalPlayer();
            if (local == null) return;

            try
            {
                SendHandshake(client, local.NetId);
            }
            catch (Exception ex)
            {
                // 只记一次：这条路径每 8 秒会走一遍，反复记会把日志刷爆
                if (!_sendFailureLogged)
                {
                    _sendFailureLogged = true;
                    AntiCheatRuntime.Log?.LogWarning(
                        $"[ACE 互认] 握手包发送失败（只提示一次）：{ex.GetType().Name} {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 发一条握手包。
        ///
        /// 只发一条，不按玩家逐个发：Among Us 的 RPC 是**广播**的，
        /// netId 只用来标识「这条 RPC 属于哪个对象」，房间里所有人都会收到。
        /// 若对 N 个玩家各发一条，接收方会收到 N 条 —— 既是 N² 的冗余流量，
        /// 也会把接收方的 RPC 洪水防护顶爆（默认 10 秒 5 次），
        /// 反倒把自己人判成刷屏。
        /// </summary>
        private static void SendHandshake(AmongUsClient client, uint netId)
        {
            var writer = client.StartRpcImmediately(netId, MagicCallId, SendOption.Reliable, -1);
            if (writer == null) return;

            writer.Write(Magic[0]);
            writer.Write(Magic[1]);
            writer.Write(Magic[2]);
            writer.Write(Magic[3]);

            client.FinishRpcImmediately(writer);
        }
    }
}
