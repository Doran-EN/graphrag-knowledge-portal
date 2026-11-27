using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ObsidianGraphRag.Shared.Services.LLM
{
    public interface ILlmClient
    {
        Task<string> ChatAsync(List<ChatMessageDto> messages, double temperature = 0.2, int? maxOutputTokens = null);
        Task<string> ExtractJsonAsync(string systemPrompt, string userPrompt);
        Task<string> ExtractStructuredJsonAsync(string systemPrompt, string userPrompt, JsonElement schema) =>
            ExtractJsonAsync(systemPrompt, userPrompt);
        Task<float[][]> GenerateEmbeddingsAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken);
    }

    public class ChatMessageDto
    {
        public string Role { get; set; } = "user"; // "system", "user", "assistant"
        public string Content { get; set; } = string.Empty;
    }
}
