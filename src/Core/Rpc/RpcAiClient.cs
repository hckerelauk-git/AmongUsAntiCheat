using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AmongUsAntiCheat.Core
{
    /// <summary>
    /// 大模型对 RPC 序列的判定结果（AI 给出）。
    /// </summary>
    public sealed class AiVerdict
    {
        /// <summary>AI 是否认为该玩家可疑。</summary>
        public bool Suspicious;

        /// <summary>置信度 0~1。</summary>
        public float Confidence;

        /// <summary>给人类看的理由（直接进日志 / UI）。</summary>
        public string Reason;

        /// <summary>AI 检出的违规类型（用于映射成本地 ViolationKind）。</summary>
        public string ViolationKind;

        public bool IsEmpty => Reason == null && !Suspicious;
    }

    /// <summary>
    /// OpenAI 兼容的 chat/completions HTTP 客户端。
    ///
    /// 通用：DeepSeek / OpenAI / Ollama / 各种自建网关，只要端点走同一套协议就能用。
    /// </summary>
    public sealed class RpcAiClient
    {
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly int _timeoutSeconds;

        public RpcAiClient(string endpoint, string apiKey, string model, int timeoutSeconds)
        {
            _endpoint = endpoint;
            _apiKey = apiKey;
            _model = model;
            _timeoutSeconds = timeoutSeconds;
        }

        public async Task<AiVerdict> AnalyzeAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeoutSeconds) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            var payload = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
                temperature = 0.1,
                max_tokens = 500,
                response_format = new { type = "json_object" },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseCompletionResponse(raw);
        }

        /// <summary>
        /// 解析 OpenAI 标准响应：
        /// { "choices": [{ "message": { "content": "{...JSON...}" } }] }
        /// </summary>
        private static AiVerdict ParseCompletionResponse(string raw)
        {
            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";

            return ParseVerdictJson(content);
        }

        /// <summary>
        /// 解析 AI 返回的 JSON（要求模型输出 json_object）：
        /// { "suspicious": true, "confidence": 0.9, "reason": "...", "violations": [{"kind":"..."}] }
        /// </summary>
        public static AiVerdict ParseVerdictJson(string json)
        {
            var verdict = new AiVerdict();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("suspicious", out var s) &&
                    (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.False))
                    verdict.Suspicious = s.GetBoolean();

                if (root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                    verdict.Confidence = Mathf.Clamp01((float)c.GetDouble());

                if (root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                    verdict.Reason = r.GetString();

                if (root.TryGetProperty("violations", out var v) && v.ValueKind == JsonValueKind.Array &&
                    v.GetArrayLength() > 0)
                {
                    var first = v[0];
                    if (first.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
                        verdict.ViolationKind = k.GetString();
                }
            }
            catch
            {
                // 解析失败：返回空 verdict，调用方会忽略
            }
            return verdict;
        }
    }
}