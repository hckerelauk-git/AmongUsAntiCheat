using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AmongUsAntiCheat.Core
{
    /// <summary>
    /// 历史记录落盘。
    ///
    /// 两个文件写在**游戏根目录**（即 Among Us.exe 所在目录）：
    ///   PlayerHistory.txt —— 每次进入对局时记录房间里有谁
    ///   CheatHistory.txt  —— 每次命中规则时记录谁、命中了什么
    ///
    /// 设计取舍：直接追加写，不做缓冲与异步。
    /// 命中事件本身很稀疏（每秒最多几次），而落盘时机恰恰是需要留证据的时候，
    /// 缓冲反而会在游戏崩溃时丢掉最关键的那几行。
    /// 写失败（目录只读等）时停用该记录，不影响游戏运行。
    /// </summary>
    internal static class HistoryLog
    {
        /// <summary>一旦写失败就置位，避免每次命中都重试并刷日志。</summary>
        private static bool _disabled;

        public static void RecordRoundPlayers(IEnumerable<string> names)
        {
            if (!(AntiCheatRuntime.Config?.RecordPlayerHistory.Value ?? false)) return;

            try
            {
                var sb = new StringBuilder("对局玩家：");
                var first = true;
                foreach (var n in names)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    if (!first) sb.Append('、');
                    sb.Append(n);
                    first = false;
                }
                Append("PlayerHistory.txt", sb.ToString());
            }
            catch { }
        }

        public static void RecordViolation(string playerName, int playerId, string kind, string detail)
        {
            if (!(AntiCheatRuntime.Config?.RecordCheatHistory.Value ?? false)) return;
            Append("CheatHistory.txt", $"{playerName}({playerId}) 命中 {kind}：{detail}");
        }

        private static void Append(string file, string line)
        {
            if (_disabled) return;

            try
            {
                var path = Path.Combine(Environment.CurrentDirectory, file);
                File.AppendAllText(path,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + line + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _disabled = true;
                AntiCheatRuntime.Log?.LogWarning(
                    $"[记录] 写入 {file} 失败，本次会话已停用记录功能：{ex.Message}");
            }
        }
    }
}
