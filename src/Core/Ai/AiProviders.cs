using System;
using System.Collections.Generic;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 一个 AI 供应商的完整描述。
    ///
    /// 设计意图：端点地址、模型名等「用户无需填写」的字段，
    /// 全部封在这个对象里。用户只需要填一把密钥。
    ///
    /// 想加供应商（比如换成别的厂商）时，只需要在这里加一行 new，
    /// 界面上会自动多出一个选项，其它代码一行都不用动。
    /// </summary>
    public sealed class AiProvider
    {
        /// <summary>配置文件里存的值（用户看到的名字）。</summary>
        public string DisplayName { get; }

        /// <summary>chat/completions 端点，用户永远不需要看到这个。</summary>
        public string Endpoint { get; }

        /// <summary>模型名，用户永远不需要看到这个。</summary>
        public string Model { get; }

        /// <summary>单行说明，用于界面展示用途。</summary>
        public string Summary { get; }

        /// <summary>密钥长什么样，界面上给个样例，用户才知道填对了没。</summary>
        public string KeyHint { get; }

        /// <summary>去哪申请密钥。界面上原样显示，让用户自己开浏览器去。</summary>
        public string SignupUrl { get; }

        /// <summary>官方文档地址。</summary>
        public string DocUrl { get; }

        /// <summary>是否需要密钥。本地模型（Ollama 之类）可以是 false。</summary>
        public bool RequiresApiKey { get; }

        public AiProvider(
            string displayName,
            string endpoint,
            string model,
            string summary,
            string keyHint,
            string signupUrl,
            string docUrl,
            bool requiresApiKey = true)
        {
            DisplayName = displayName;
            Endpoint = endpoint;
            Model = model;
            Summary = summary;
            KeyHint = keyHint;
            SignupUrl = signupUrl;
            DocUrl = docUrl;
            RequiresApiKey = requiresApiKey;
        }

        /// <summary>
        /// 整理用户填进来的密钥。
        ///
        /// 现实里复制粘贴经常会带上首尾空格、换行、制表符，
        /// 而 HTTP 头里带这些字符会被服务端直接拒绝，
        /// 表现为「密钥正确却返回 401」，排查困难。此处统一清理。
        /// </summary>
        public string NormalizeKey(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            return raw.Trim()
                       .Replace("\r", string.Empty)
                       .Replace("\n", string.Empty)
                       .Replace("\t", string.Empty)
                       .Trim();
        }

        /// <summary>配置是否够用：需要密钥的供应商必须填了密钥。</summary>
        public bool IsUsableWith(string apiKey) =>
            !RequiresApiKey || !string.IsNullOrEmpty(NormalizeKey(apiKey));
    }

    /// <summary>
    /// 供应商目录。
    ///
    /// 目前只注册 DeepSeek 一个——它的价格低、国内直连不用梯子、
    /// 且完全兼容所需接口协议，为该场景的默认选择。
    ///
    /// 故意不做成「用户自己填地址」的形式：
    /// 否则用户需面对一串 URL 与模型名，任一字符错误即导致 404，
    /// 而普通用户无法判断是配置错误还是服务异常。
    /// 端点由代码保证正确，用户只需要一把密钥。
    /// </summary>
    public static class AiProviders
    {
        /// <summary>DeepSeek 官方。</summary>
        public static readonly AiProvider DeepSeek = new AiProvider(
            displayName: "DeepSeek",
            endpoint: "https://api.deepseek.com/v1/chat/completions",
            model: "deepseek-chat",
            summary: "国内直连不用梯子，按量计费很便宜。注册送免费额度。",
            keyHint: "sk- 开头的一长串字母数字",
            signupUrl: "https://platform.deepseek.com/",
            docUrl: "https://api-docs.deepseek.com/");

        /// <summary>首次启用时默认选中的那个。</summary>
        public const string DefaultDisplayName = "DeepSeek";

        private static readonly AiProvider[] AllProviders =
        {
            DeepSeek,
        };

        private static readonly Dictionary<string, AiProvider> Registry = BuildRegistry();

        private static Dictionary<string, AiProvider> BuildRegistry()
        {
            var map = new Dictionary<string, AiProvider>(AllProviders.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var p in AllProviders) map[p.DisplayName] = p;
            return map;
        }

        /// <summary>全部已注册供应商，供界面列出来。</summary>
        public static IReadOnlyList<AiProvider> All => AllProviders;

        /// <summary>按显示名查找；找不到返回 null。</summary>
        public static bool TryGet(string displayName, out AiProvider provider)
        {
            provider = null;
            if (string.IsNullOrWhiteSpace(displayName)) return false;
            return Registry.TryGetValue(displayName.Trim(), out provider);
        }

        /// <summary>
        /// 按显示名查找，找不到时回退到默认供应商。
        ///
        /// 之所以不返回 null：配置文件里存的值可能是过时的
        /// （比如上个版本写了「OpenAI」，现在目录里没这家了），
        /// 这时候应该安静地退回默认值，而不是让整个 AI 功能报错。
        /// </summary>
        public static AiProvider Resolve(string displayName) =>
            TryGet(displayName, out var p) ? p : DeepSeek;
    }
}
