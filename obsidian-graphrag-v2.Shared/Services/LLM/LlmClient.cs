using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace ObsidianGraphRag.Shared.Services.LLM
{
    public class LlmClient : ILlmClient
    {
        private const string DefaultModel = "qwen3.5:4b";
        private static readonly JsonSerializerOptions PayloadOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        private readonly HttpClient _httpClient;
        private string _baseUrl = "http://localhost:11434"; // Default to Ollama, configurable
        private string _model = DefaultModel;
        private string _apiKey = string.Empty;

        public LlmClient(HttpClient httpClient, IConfiguration? config = null)
        {
            _httpClient = httpClient;
            if (config != null)
            {
                var url = config["Ollama:BaseUrl"];
                if (!string.IsNullOrWhiteSpace(url)) _baseUrl = url.TrimEnd('/');

                var model = config["Ollama:DefaultModel"];
                if (!string.IsNullOrWhiteSpace(model)) _model = model;
            }
        }

        public void Configure(string baseUrl, string model, string apiKey = "")
        {
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl.TrimEnd('/');
            _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
            _apiKey = apiKey;
        }

        public async Task<string> ChatAsync(List<ChatMessageDto> messages, double temperature = 0.2, int? maxOutputTokens = null)
        {
            try
            {
                var payload = new
                {
                    model = _model,
                    messages = messages,
                    stream = false,
                    think = _model.Equals("qwen3.5:4b", StringComparison.OrdinalIgnoreCase) ? (bool?)false : null,
                    options = new { temperature, num_predict = maxOutputTokens }
                };

                using var request = CreateRequest("/api/chat", payload);
                using var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var jsonStr = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonStr);
                    if (doc.RootElement.TryGetProperty("message", out var msgEl) &&
                        msgEl.TryGetProperty("content", out var contentEl))
                    {
                        return contentEl.GetString() ?? string.Empty;
                    }

                    return "No response received from LLM.";
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    if (errorBody.Contains("model", StringComparison.OrdinalIgnoreCase) &&
                        errorBody.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"[LLM Request Error] Ollama could not find model {_model}.";
                    }
                }
                else if (response.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
                {
                    return $"[LLM Request Error] The LLM endpoint returned HTTP {(int)response.StatusCode}.";
                }

                // Try the OpenAI-compatible route when the native route is unavailable.
                var openaiPayload = new { model = _model, messages, temperature, max_tokens = maxOutputTokens };
                using var openaiRequest = CreateRequest("/v1/chat/completions", openaiPayload);
                using var openaiResponse = await _httpClient.SendAsync(openaiRequest);
                if (!openaiResponse.IsSuccessStatusCode)
                {
                    return $"[LLM Request Error] The LLM endpoint returned HTTP {(int)openaiResponse.StatusCode}.";
                }

                var openaiJson = await openaiResponse.Content.ReadAsStringAsync();
                using var openaiDoc = JsonDocument.Parse(openaiJson);
                var choices = openaiDoc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0)
                {
                    return choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
                }
            }
            catch (OperationCanceledException)
            {
                return $"[LLM Timeout] {_model} did not respond within {_httpClient.Timeout.TotalSeconds:0} seconds.";
            }
            catch (HttpRequestException ex)
            {
                return $"[LLM Connection Notice] Could not complete the HTTP request to {_baseUrl}. {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"[LLM Response Error] Could not read the response from {_baseUrl}. {ex.Message}";
            }

            return "No response received from LLM.";
        }

        private HttpRequestMessage CreateRequest(string path, object payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, PayloadOptions), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(_apiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            }

            return request;
        }

        public async Task<string> ExtractJsonAsync(string systemPrompt, string userPrompt)
        {
            var messages = new List<ChatMessageDto>
            {
                new() { Role = "system", Content = systemPrompt + "\nReturn strictly valid JSON only. Do not wrap in extra markdown or commentary." },
                new() { Role = "user", Content = userPrompt }
            };

            var raw = await ChatAsync(messages, temperature: 0.1);
            return raw.StartsWith("[LLM ", StringComparison.Ordinal) ? raw : CleanJson(raw);
        }

        public async Task<float[][]> GenerateEmbeddingsAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
        {
            var payload = new { model = "nomic-embed-text", input = inputs };
            using var request = CreateRequest("/api/embed", payload);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("embeddings", out var embeddings))
                throw new JsonException("Ollama returned no embeddings.");

            var vectors = embeddings.Deserialize<float[][]>();
            if (vectors is null || vectors.Length != inputs.Count ||
                vectors.Any(vector => vector is null || vector.Length != 768))
                throw new JsonException("Ollama returned unexpected embedding dimensions.");

            return vectors;
        }

        private static string CleanJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";
            text = text.Trim();

            // Strip <think>...</think> reasoning blocks from thinking models
            text = Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();

            // Strip markdown code fences ```json ... ```
            if (text.Contains("```"))
            {
                var fenceMatch = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
                if (fenceMatch.Success)
                {
                    text = fenceMatch.Groups[1].Value.Trim();
                }
            }

            // Extract outermost JSON block if still surrounded by text
            int firstBrace = text.IndexOf('{');
            int lastBrace = text.LastIndexOf('}');
            int firstBracket = text.IndexOf('[');
            int lastBracket = text.LastIndexOf(']');

            if (firstBrace >= 0 && lastBrace > firstBrace && (firstBracket < 0 || firstBrace < firstBracket))
            {
                text = text.Substring(firstBrace, lastBrace - firstBrace + 1);
            }
            else if (firstBracket >= 0 && lastBracket > firstBracket)
            {
                text = text.Substring(firstBracket, lastBracket - firstBracket + 1);
            }

            return text;
        }
    }
}
