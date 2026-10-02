using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AmongUs.GameOptions;
using ApexCheatEnder.Core;
using UnityEngine;

namespace ApexCheatEnder
{
    /// <summary>
    /// 游戏 API 桥接层。
    ///
    /// 存在的唯一理由：Among Us 每次更新都可能改字段名，而 IL2CPP 下访问不存在的成员
    /// 会抛异常甚至直接崩进程。所以这里把所有游戏 API 访问集中起来，
    /// 每一处都做「多路径降级 + 异常吞掉」，让插件在游戏更新后最多失去某项检测能力，
    /// 而不是整个挂掉。
    /// </summary>
    public static class GameBridge
    {
        /// <summary>墙体所在的物理层掩码，用于穿墙检测。拿不到时为 0。</summary>
        private static int _wallLayerMask = -1;

        /// <summary>玩家碰撞层掩码，穿墙检测时用于排除玩家自身。</summary>
        private static int _playerLayerMask = -1;

        // ==================================================================
        //  对局状态
        // ==================================================================

        /// <summary>是否处于可检测的对局中（在大厅或主菜单时为 false）。</summary>
        public static bool IsInGame
        {
            get
            {
                try
                {
                    if (AmongUsClient.Instance == null) return false;
                    if (!AmongUsClient.Instance.IsGameStarted) return false;
                    return ShipStatus.Instance != null;
                }
                catch { return false; }
            }
        }

        /// <summary>是否正在开会议。</summary>
        public static bool IsInMeeting
        {
            get
            {
                try { return MeetingHud.Instance != null; }
                catch { return false; }
            }
        }

        /// <summary>自己是否是房主。只有房主才有处置权限。</summary>
        public static bool IsHost
        {
            get
            {
                try { return AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost; }
                catch { return false; }
            }
        }

        /// <summary>本地玩家的 ClientId，用于踢人时排除自己。</summary>
        public static int LocalClientId
        {
            get
            {
                try { return AmongUsClient.Instance?.ClientId ?? -1; }
                catch { return -1; }
            }
        }

        // ==================================================================
        //  玩家枚举
        // ==================================================================

        /// <summary>获取当前所有玩家。失败时返回空列表，调用方无需判空。</summary>
        public static List<PlayerControl> GetPlayers()
        {
            var result = new List<PlayerControl>(16);
            GetPlayersInto(result);
            return result;
        }

        /// <summary>
        /// 把当前玩家填充进调用方提供的缓冲区（会先 Clear）。
        ///
        /// 热路径专用：采样每 0.1 秒跑一次，用 <see cref="GetPlayers"/> 会产生
        /// 每秒 10 个列表的垃圾。调用方必须**立即消费**缓冲区内容，
        /// 不要跨帧持有它——下一次调用会把它清空。
        /// </summary>
        public static void GetPlayersInto(List<PlayerControl> buffer)
        {
            if (buffer == null) return;
            buffer.Clear();
            try
            {
                var all = PlayerControl.AllPlayerControls;
                if (all == null) return;

                for (var i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    if (p != null) buffer.Add(p);
                }
            }
            catch { /* 忽略：游戏未初始化时该集合可能不可用 */ }
        }

        public static PlayerControl GetLocalPlayer()
        {
            try { return PlayerControl.LocalPlayer; }
            catch { return null; }
        }

        /// <summary>取玩家 Id。IL2CPP 下 PlayerId 是 byte，这里统一转 int。</summary>
        public static int GetPlayerId(PlayerControl player)
        {
            try { return player?.PlayerId ?? -1; }
            catch { return -1; }
        }

        /// <summary>
        /// 清除玩家名里混入的 BBCode / 富文本标签。
        ///
        /// 必要性：Reactor、BetterAmongUs 等模组允许玩家在昵称里使用 BBCode。
        /// 不清洗就把原始字符串塞进 UI，会显示成 `&lt;size=1.6&&lt;#1b313f&gt;...` 这种乱码；
        /// 即使开启 rich text，非法的标签也会被原样显示。
        /// 在数据层一次性清洗最干净。
        /// </summary>
        private static readonly Regex TagPattern =
            new Regex("<[^>]*>", RegexOptions.Compiled);

        public static string GetPlayerName(PlayerControl player)
        {
            try
            {
                var name = player?.Data?.PlayerName ?? "?";
                return string.IsNullOrEmpty(name) ? "?" : TagPattern.Replace(name, string.Empty);
            }
            catch { return "?"; }
        }

        /// <summary>获取玩家当前的世界坐标。</summary>
        public static GameVec2 GetPosition(PlayerControl player)
        {
            try
            {
                if (player?.transform == null) return GameVec2.Zero;
                var pos = player.transform.position;
                return new GameVec2(pos.x, pos.y);
            }
            catch { return GameVec2.Zero; }
        }

        public static bool IsDead(PlayerControl player)
        {
            try { return player?.Data?.IsDead ?? true; }
            catch { return true; }
        }

        public static bool IsDisconnected(PlayerControl player)
        {
            try { return player?.Data?.Disconnected ?? false; }
            catch { return false; }
        }

        /// <summary>玩家是否在通风管内。</summary>
        public static bool IsInVent(PlayerControl player)
        {
            try { return player != null && player.inVent; }
            catch { return false; }
        }

        /// <summary>
        /// 判断玩家是否为内鬼阵营。
        ///
        /// 首选 RoleBehaviour.IsImpostor —— 这是游戏自己判定阵营用的权威属性，
        /// 无论模组加了什么自定义角色（这版游戏里就有 Detective / Viper / Judge），
        /// 它都会给出正确结果。
        /// 仅在该属性不可用时，才退回按 RoleType 枚举做保守判断。
        /// </summary>
        public static bool IsImpostor(PlayerControl player)
        {
            if (player == null) return false;

            // 路径 1（首选）：角色对象的权威判定
            try
            {
                var role = player.Data?.Role;
                if (role != null) return role.IsImpostor;
            }
            catch { }

            // 路径 2（降级）：只认明确属于内鬼阵营的原生角色，
            // 对未知的模组角色一律按「非内鬼」处理，宁可漏报也不误伤。
            try
            {
                switch (player.Data.RoleType)
                {
                    case RoleTypes.Impostor:
                    case RoleTypes.Shapeshifter:
                    case RoleTypes.Phantom:
                    case RoleTypes.ImpostorGhost:
                        return true;
                    default:
                        return false;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 读取玩家「能否使用通风管」的角色能力。
        ///
        /// ⚠️ 必须用 Role.CanVent，**不能**用 IsImpostor 代替：
        /// 本版游戏存在 Viper（船员阵营、但被允许钻管道）这类角色，
        /// 拿阵营去判「非内鬼进管道 = 作弊」会把 Viper 误判成 Critical 直接踢掉。
        /// 拿不到角色对象时返回 false（表示「未知」），由调用方决定是否放行——
        /// 遵循宁可漏报不误伤的原则。
        /// </summary>
        /// <param name="player">目标玩家。</param>
        /// <param name="canVent">角色能力；仅在返回 true 时有效。</param>
        /// <returns>是否成功取到角色能力（false = 角色信息不可用）。</returns>
        public static bool TryGetCanVent(PlayerControl player, out bool canVent)
        {
            canVent = true;
            if (player == null) return false;

            try
            {
                var role = player.Data?.Role;
                if (role != null)
                {
                    canVent = role.CanVent;
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>玩家当前能否移动。</summary>
        public static bool CanMove(PlayerControl player)
        {
            try { return player != null && player.CanMove; }
            catch { return true; }
        }

        // ==================================================================
        //  游戏模式与角色能力
        // ==================================================================

        /// <summary>取飞船状态对象；不在对局中时为 null。</summary>
        public static ShipStatus GetShipStatus()
        {
            try { return ShipStatus.Instance; }
            catch { return null; }
        }

        /// <summary>
        /// 是否为躲猫猫（Hide &amp; Seek）模式。
        ///
        /// 用途：躲猫猫的规则与经典模式差别很大（Seeker 能钻管道、船员不能报告尸体、
        /// 破坏系统行为不同），凡是模式相关的判定都必须先问这一句。
        /// 拿不到模式信息时返回 false（按经典模式处理）。
        /// </summary>
        public static bool IsHideAndSeek
        {
            get
            {
                try
                {
                    var opts = GameOptionsManager.Instance?.CurrentGameOptions;
                    if (opts == null) return false;
                    return opts.GameMode == GameModes.HideNSeek;
                }
                catch { return false; }
            }
        }

        /// <summary>取玩家的角色类型。拿不到时返回 <c>null</c>。</summary>
        public static RoleTypes? GetRoleType(PlayerControl player)
        {
            try
            {
                if (player?.Data == null) return null;
                return player.Data.RoleType;
            }
            catch { return null; }
        }

        /// <summary>
        /// 玩家是否为变形者（Shapeshifter）。
        /// 信息不可用时返回 false —— 调用方**必须**配合 <see cref="IsRoleKnown"/> 使用，
        /// 否则会把角色未就绪的正常玩家误判成作弊。
        /// </summary>
        public static bool IsShapeshifter(PlayerControl player)
        {
            try { return player?.Data?.RoleType == RoleTypes.Shapeshifter; }
            catch { return false; }
        }

        /// <summary>玩家是否为守护天使（GuardianAngel）。信息不可用时返回 false。</summary>
        public static bool IsGuardianAngel(PlayerControl player)
        {
            try { return player?.Data?.RoleType == RoleTypes.GuardianAngel; }
            catch { return false; }
        }

        /// <summary>角色信息是否可读（Role 对象已就绪）。不可读时不应做角色相关判罚。</summary>
        public static bool IsRoleKnown(PlayerControl player)
        {
            try { return player?.Data?.Role != null; }
            catch { return false; }
        }

        // ==================================================================
        //  通风管
        // ==================================================================

        /// <summary>当前地图的通风管总数。拿不到时返回 -1（表示「未知」）。</summary>
        public static int GetVentCount()
        {
            try
            {
                var vents = ShipStatus.Instance?.AllVents;
                return vents?.Length ?? -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 校验通风管编号是否在合法范围内。
        /// 返回 <c>null</c> 表示无法判断（不应据此判罚）。
        /// </summary>
        public static bool? IsValidVentId(int ventId)
        {
            var count = GetVentCount();
            if (count < 0) return null;
            return ventId >= 0 && ventId < count;
        }

        // ==================================================================
        //  游戏设置读取
        // ==================================================================

        /// <summary>
        /// 当前对局允许的最大移动速度。
        ///
        /// 优先读 PlayerPhysics.TrueSpeed —— 这是游戏自己算出来的「理论最大速度」，
        /// 已经包含了速度倍率设置和角色修正，比自己拿基础值乘倍率准确得多。
        /// </summary>
        public static float GetMaxAllowedSpeed(PlayerControl reference = null)
        {
            // 路径 1（首选）：物理组件的理论速度
            try
            {
                var player = reference ?? GetLocalPlayer();
                var physics = player?.MyPhysics;
                if (physics != null)
                {
                    var speed = physics.TrueSpeed;
                    if (speed > 0.01f) return speed;

                    speed = physics.Speed;
                    if (speed > 0.01f) return speed;
                }
            }
            catch { }

            // 路径 2（降级）：读速度倍率自行估算
            try
            {
                var speedMod = GetFloatOption("PlayerSpeedMod", 1.0f);
                if (speedMod > 0.01f) return BaseMoveSpeed * speedMod;
            }
            catch { }

            return BaseMoveSpeed;
        }

        /// <summary>Among Us 在速度倍率为 1.0 时的基准移动速度。</summary>
        private const float BaseMoveSpeed = 2.5f;

        /// <summary>
        /// 击杀距离档位表（世界单位），对应设置里的 Short / Medium / Long。
        ///
        /// 游戏没有把这个表暴露成可读取的静态字段（PlayerControl.KillDistances 在当前版本已不存在），
        /// 所以在此固化。判定时会再叠加一个容差来吸收玩家碰撞半径与网络延迟。
        /// </summary>
        private static readonly float[] KillDistanceTable = { 1.0f, 1.8f, 2.5f };

        /// <summary>读取击杀距离设置（世界单位）。</summary>
        public static float GetAllowedKillDistance()
        {
            try
            {
                var index = GetIntOption("KillDistance", 1);
                if (index >= 0 && index < KillDistanceTable.Length)
                    return KillDistanceTable[index];
            }
            catch { }
            return KillDistanceTable[1];
        }

        /// <summary>读取击杀冷却（秒）。</summary>
        public static float GetKillCooldown()
        {
            try { return GetFloatOption("KillCooldown", 25f); }
            catch { return 25f; }
        }

        /// <summary>从当前对局选项里读一个浮点项（枚举名不存在时返回兜底值）。</summary>
        private static float GetFloatOption(string optionName, float fallback)
        {
            try
            {
                var options = GameOptionsManager.Instance?.CurrentGameOptions;
                if (options == null) return fallback;
                if (!Enum.TryParse(optionName, out FloatOptionNames name)) return fallback;
                return options.GetFloat(name);
            }
            catch { return fallback; }
        }

        /// <summary>从当前对局选项里读一个整数项。</summary>
        private static int GetIntOption(string optionName, int fallback)
        {
            try
            {
                var options = GameOptionsManager.Instance?.CurrentGameOptions;
                if (options == null) return fallback;
                if (!Enum.TryParse(optionName, out Int32OptionNames name)) return fallback;
                return options.GetInt(name);
            }
            catch { return fallback; }
        }

        // ==================================================================
        //  物理检测
        // ==================================================================

        /// <summary>
        /// 判断某个世界坐标是否位于墙体碰撞体内部。
        /// 用于穿墙检测。拿不到层信息时返回 false（即不做判定，宁可漏报不误报）。
        /// </summary>
        public static bool IsInsideWall(GameVec2 point)
        {
            try
            {
                if (_wallLayerMask == -1)
                {
                    var shipLayer = LayerMask.NameToLayer("Ship");
                    var playersLayer = LayerMask.NameToLayer("Players");
                    // 层不存在时置 0，表示禁用该检测
                    _wallLayerMask = shipLayer >= 0 ? (1 << shipLayer) : 0;
                    _playerLayerMask = playersLayer >= 0 ? ~(1 << playersLayer) : ~0;
                }

                if (_wallLayerMask == 0) return false;

                var hit = Physics2D.OverlapPoint(new Vector2(point.X, point.Y), _wallLayerMask);
                return hit != null;
            }
            catch { return false; }
        }

        /// <summary>重置缓存的物理层掩码（场景切换后层信息可能重建）。</summary>
        public static void InvalidateLayerCache()
        {
            _wallLayerMask = -1;
            _playerLayerMask = -1;
        }

        // ==================================================================
        //  处置动作
        // ==================================================================

        /// <summary>
        /// 踢出指定玩家。只有房主调用才有效。
        /// </summary>
        /// <param name="clientId">目标玩家的 ClientId。</param>
        /// <param name="ban">是否同时封禁。</param>
        /// <returns>是否成功发起踢出。</returns>
        public static bool KickPlayer(int clientId, bool ban = false)
        {
            try
            {
                if (!IsHost) return false;
                if (AmongUsClient.Instance == null) return false;
                if (clientId == LocalClientId) return false;

                AmongUsClient.Instance.KickPlayer(clientId, ban);
                return true;
            }
            catch { return false; }
        }

        /// <summary>按 PlayerId 找对应的 ClientId。</summary>
        public static int GetClientIdByPlayerId(int playerId)
        {
            try
            {
                var data = GameData.Instance;
                if (data?.AllPlayers == null) return -1;

                for (var i = 0; i < data.AllPlayers.Count; i++)
                {
                    var info = data.AllPlayers[i];
                    if (info != null && info.PlayerId == playerId) return info.ClientId;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>按 PlayerId 找到 PlayerControl。</summary>
        public static PlayerControl GetPlayerById(int playerId)
        {
            try
            {
                var players = PlayerControl.AllPlayerControls;
                if (players == null) return null;

                for (var i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p != null && p.PlayerId == playerId) return p;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 按任务 Id 查该任务点的世界坐标。
        /// 用于「远程做任务」检测——需要知道玩家提交任务时应该站在哪里。
        ///
        /// 走玩家自己的 myTasks 列表，而不是 ShipStatus 的 ShortTasks/LongTasks 数组：
        /// 后者的类型是 Il2CppReferenceArray&lt;T&gt;，其泛型约束在当前 interop 版本下
        /// 无法被 NormalPlayerTask / PlayerTask 满足（编译期即失败）。
        /// myTasks 是普通的 Il2Cpp List，访问稳定且语义更直接——就是「这个玩家要做的任务」。
        /// </summary>
        public static GameVec2? GetTaskPosition(PlayerControl player, int taskId)
        {
            try
            {
                var tasks = player?.myTasks;
                if (tasks == null) return null;

                for (var i = 0; i < tasks.Count; i++)
                {
                    var task = tasks[i];
                    if (task == null || task.Id != (uint)taskId) continue;

                    var p = task.transform.position;
                    return new GameVec2(p.x, p.y);
                }
            }
            catch { }
            return null;
        }
    }
}
