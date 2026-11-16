using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace ObsidianGraphRag.Shared.Services.N8n
{
    public class N8nCrawlService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _webhookPath;
        private readonly Func<string, string>? _crawlToken;

        public bool IsBusy { get; private set; }
        public string? LastStatusMessage { get; private set; }
        public event Action? OnStatusChanged;

        public N8nCrawlService(HttpClient httpClient, IConfiguration? configuration = null, Func<string, string>? crawlToken = null)
        {
            _httpClient = httpClient;
            _baseUrl = configuration?["N8n:BaseUrl"] ?? "http://localhost:5678";
            _webhookPath = configuration?["N8n:WebhookPath"] ?? "/webhook/crawl4ai-ingest";
            _crawlToken = crawlToken;
        }

        public async Task<bool> CheckHealthAsync()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                var resp = await _httpClient.GetAsync($"{_baseUrl.TrimEnd('/')}/healthz", cts.Token);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<(bool Success, string Message)> TriggerCrawlAsync(string targetWebsite)
        {
            if (string.IsNullOrWhiteSpace(targetWebsite))
            {
                return (false, "Target website URL cannot be empty.");
            }

            IsBusy = true;
            LastStatusMessage = $"Triggering n8n crawl for {targetWebsite}...";
            NotifyStatus();

            try
            {
                var webhookUrl = $"{_baseUrl.TrimEnd('/')}{_webhookPath}";
                var payload = new { targetWebsite = targetWebsite.Trim(), crawlToken = _crawlToken?.Invoke(targetWebsite.Trim()) };
                var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                var resp = await _httpClient.PostAsync(webhookUrl, jsonContent, cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    LastStatusMessage = $"Crawl started for {targetWebsite}. Markdown files will appear in the knowledge base.";
                    NotifyStatus();
                    return (true, LastStatusMessage);
                }
                else
                {
                    LastStatusMessage = $"The crawler returned HTTP {(int)resp.StatusCode}. Check that the portal workflow is published in n8n.";
                    NotifyStatus();
                    return (false, LastStatusMessage);
                }
            }
            catch (ArgumentException ex)
            {
                LastStatusMessage = ex.Message;
                NotifyStatus();
                return (false, LastStatusMessage);
            }
            catch (Exception ex)
            {
                LastStatusMessage = $"Failed to connect to n8n: {ex.Message}";
                NotifyStatus();
                return (false, LastStatusMessage);
            }
            finally
            {
                IsBusy = false;
                NotifyStatus();
            }
        }

        private void NotifyStatus() => OnStatusChanged?.Invoke();
    }
}
