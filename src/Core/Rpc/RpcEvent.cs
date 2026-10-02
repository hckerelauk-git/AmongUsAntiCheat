using System;
using System.Collections.Generic;
using System.Text;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 单条 RPC 事件记录。
    ///
    /// 字段刻意保持可序列化：日后无论导出 JSON 还是送进 LLM，都可以直接序列化。
    /// </summary>
    [Serializable]
    public struct RpcEvent
    {
        /// <summary>游戏内时间（秒）。</summary>
        public float Time;

        /// <summary>RPC 名（如 "MurderPlayer" / "CompleteTask" / "EnterVent" / "SnapTo"）。</summary>
        public string Type;

        /// <summary>附加参数（JSON 格式）。允许为空。</summary>
        public string ArgsJson;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(Time.ToString("F2")).Append("s] ").Append(Type);
            if (!string.IsNullOrEmpty(ArgsJson)) sb.Append(' ').Append(ArgsJson);
            return sb.ToString();
        }
    }
}