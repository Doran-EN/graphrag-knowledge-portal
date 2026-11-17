using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ObsidianGraphRag.Shared.Services.N8n;

public sealed class N8nCrawlService
{
    public static readonly TimeSpan GrantLifetime = TimeSpan.FromHours(2);

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _webhookPath;
    private readonly Func<string, string>? _crawlToken;
    private readonly object _gate = new();
    private CrawlRun? _activeRun;

    public CrawlStatus? Status { get; private set; }
    public bool IsBusy => Status?.IsRunning == true;
    public event Action? OnStatusChanged;

    public N8nCrawlService(HttpClient httpClient, IConfiguration? configuration = null, Func<string, string>? crawlToken = null)
    {
        _httpClient = httpClient;
        _baseUrl = (configuration?["N8n:BaseUrl"] ?? "http://localhost:5678").TrimEnd('/');
        _webhookPath = configuration?["N8n:WebhookPath"] ?? "/webhook/crawl4ai-ingest";
        _crawlToken = crawlToken;
    }

    public async Task<bool> CheckHealthAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await _httpClient.GetAsync($"{_baseUrl}/healthz", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task TriggerCrawlAsync(string targetWebsite)
    {
        var url = targetWebsite.Trim();
        var run = new CrawlRun();
        lock (_gate)
        {
            if (_activeRun is not null)
                return;

            _activeRun = run;
            Status = new CrawlStatus("Crawling website…", IsRunning: true);
        }
        OnStatusChanged?.Invoke();

        CrawlStatus result;
        try
        {
            var token = _crawlToken?.Invoke(url);
            lock (_gate)
                run.Token = token;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}{_webhookPath}", new { targetWebsite = url, crawlToken = token }, timeout.Token);

            if (!response.IsSuccessStatusCode)
                result = Failed($"The crawler returned HTTP {(int)response.StatusCode}. Check that the portal workflow is published in n8n.");
            else if (token is null)
                result = new CrawlStatus("Crawl started. Refresh sources once it finishes.");
            else
                result = await run.Completion.Task.WaitAsync(GrantLifetime);
        }
        catch (ArgumentException error)
        {
            result = Failed(error.Message);
        }
        catch (TimeoutException)
        {
            result = Failed("No completion report arrived. Check the workflow in n8n before you retry.");
        }
        catch (OperationCanceledException)
        {
            result = Failed("n8n did not confirm that the crawl started. Check the workflow before you retry.");
        }
        catch (HttpRequestException)
        {
            result = Failed("Could not connect to n8n. Check that the crawler is running and try again.");
        }

        lock (_gate)
        {
            _activeRun = null;
            Status = result;
        }
        OnStatusChanged?.Invoke();
    }

    public void RecordPageSaved(string token, string fileName)
    {
        lock (_gate)
        {
            if (FindRun(token) is { } run)
                run.SavedPages.Add(fileName);
        }
    }

    public void RecordPageDeleted(string token, string fileName)
    {
        lock (_gate)
        {
            if (FindRun(token) is not { } run)
                return;

            run.DeletedPages.Add(fileName);
            CompleteIfReady(run);
        }
    }

    public void RecordReport(string token, string site, string reportPath)
    {
        lock (_gate)
        {
            if (FindRun(token) is not { } run)
                return;

            try
            {
                using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
                var summary = report.RootElement.GetProperty("summary");
                var staleFiles = report.RootElement.GetProperty("staleUrlsDeleted").EnumerateArray()
                    .Select(item => item.GetProperty("fileName").GetString()!).ToHashSet(StringComparer.Ordinal);
                run.Report = new CrawlReport(site, Path.GetDirectoryName(reportPath)!, summary.GetProperty("failedCount").GetInt32(),
                    summary.GetProperty("unchangedSkippedCount").GetInt32(), staleFiles);
                CompleteIfReady(run);
            }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                run.Completion.TrySetResult(Failed("The completion report could not be read. Check the workflow in n8n before you retry."));
            }
        }
    }

    private CrawlRun? FindRun(string token) => _activeRun?.Token == token ? _activeRun : null;

    private static void CompleteIfReady(CrawlRun run)
    {
        // n8n writes the report before it deletes stale Markdown.
        if (run.Report is not { } report || !report.StaleFiles.IsSubsetOf(run.DeletedPages))
            return;

        var saved = run.SavedPages.Except(run.DeletedPages).Count();
        var retained = Directory.EnumerateFiles(report.SitePath, "*.md").Any();
        var savedLabel = saved == 1 ? "1 page saved" : $"{saved} pages saved";
        var failedLabel = report.Failed == 1 ? "1 page failed" : $"{report.Failed} pages failed";
        CrawlStatus status;
        if (!retained || (saved == 0 && report.Failed > 0))
        {
            var details = report.Failed > 0 ? $" {failedLabel}." : "";
            status = Failed($"No pages were saved.{details} Check the URL and retry the crawl.");
        }
        else if (report.Failed > 0)
        {
            status = new CrawlStatus($"Ready to ingest with page errors. {savedLabel}, {failedLabel}.",
                report.Site, NeedsAttention: true);
        }
        else
        {
            var skipped = report.Skipped > 0 ? $", {report.Skipped} unchanged" : "";
            status = new CrawlStatus($"Ready to ingest. {savedLabel}{skipped}.", report.Site);
        }

        run.Completion.TrySetResult(status);
    }

    private static CrawlStatus Failed(string message) => new($"Crawl failed. {message}", NeedsAttention: true);

    public sealed record CrawlStatus(string Message, string? IngestSite = null, bool IsRunning = false, bool NeedsAttention = false);

    private sealed class CrawlRun
    {
        public string? Token { get; set; }
        public HashSet<string> SavedPages { get; } = new(StringComparer.Ordinal);
        public HashSet<string> DeletedPages { get; } = new(StringComparer.Ordinal);
        public CrawlReport? Report { get; set; }
        public TaskCompletionSource<CrawlStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record CrawlReport(string Site, string SitePath, int Failed, int Skipped, HashSet<string> StaleFiles);
}
