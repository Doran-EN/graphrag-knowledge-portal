using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ObsidianGraphRag.Shared.Services.N8n;

public sealed class N8nCrawlService
{
    public const int ProtocolVersion = 3;
    private static readonly TimeSpan NoUpdateLimit = TimeSpan.FromSeconds(60);

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

    public async Task TriggerCrawlAsync(string targetWebsite, CrawlScope scope = CrawlScope.SinglePage)
    {
        var url = targetWebsite.Trim();
        using var submission = new CancellationTokenSource();
        var run = new CrawlRun(submission);
        lock (_gate)
        {
            if (_activeRun is not null)
                return;

            _activeRun = run;
            UpdateProgress(run, CrawlStage.Starting);
        }
        OnStatusChanged?.Invoke();

        CrawlStatus result;
        try
        {
            var token = _crawlToken?.Invoke(url);
            lock (_gate)
                run.Token = token;

            submission.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}{_webhookPath}", new { targetWebsite = url, crawlToken = token,
                    scope = scope == CrawlScope.Website ? "website" : "single" }, submission.Token);

            if (!response.IsSuccessStatusCode)
                result = Failed($"The crawler returned HTTP {(int)response.StatusCode}. Check that the portal workflow is published in n8n.");
            else if (token is null)
                result = new CrawlStatus("Crawl started. Refresh sources once it finishes.");
            else
            {
                RecordActivity(token, CrawlStage.WaitingForWorkflow);
                result = await WaitForReportAsync(run);
            }
        }
        catch (ArgumentException error)
        {
            result = Failed(error.Message);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                result = FinishWithIssue(run, run.Stage switch
                {
                    CrawlStage.Starting or CrawlStage.WaitingForWorkflow => "The crawl workflow did not connect.",
                    CrawlStage.CollectingPages => "Website discovery timed out.",
                    CrawlStage.Finalizing => "The workflow did not finish reporting the crawl.",
                    _ when run.SavedPages.Count == 0 => "The crawler timed out before it could save the first page.",
                    _ => "The crawler stopped reporting progress."
                });
                run.Completion.TrySetResult(result);
            }
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
            if (run.Completion.Task.IsCompletedSuccessfully)
                result = run.Completion.Task.Result;
            _activeRun = null;
            Status = result with { Elapsed = DateTimeOffset.UtcNow - run.StartedAt };
        }
        OnStatusChanged?.Invoke();
    }

    public void CancelCrawl(DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            if (_activeRun is not { } run || run.StartedAt != startedAt)
                return;

            var saved = run.SavedPages.Except(run.DeletedPages).Count();
            var message = saved == 0 ? "Crawl canceled." :
                $"Crawl canceled. {saved} {(saved == 1 ? "page" : "pages")} ready to ingest.";
            if (run.Completion.TrySetResult(new CrawlStatus(message, saved > 0 ? run.Site : null, NeedsAttention: true)))
                run.Submission.Cancel();
        }
    }

    private async Task<CrawlStatus> WaitForReportAsync(CrawlRun run)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (_gate)
            {
                remaining = run.LastUpdateAt + NoUpdateLimit - DateTimeOffset.UtcNow;
            }
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException();

            try { return await run.Completion.Task.WaitAsync(remaining); }
            catch (TimeoutException) { /* A callback may have moved the inactivity deadline. */ }
        }
    }

    public void RunWhileIdle(Action operation)
    {
        lock (_gate)
        {
            if (_activeRun is not null)
                throw new InvalidOperationException("Wait for the active crawl to finish before publishing this website.");
            operation();
        }
    }

    public bool VerifyWorkflow(string token, string site, int version)
    {
        lock (_gate)
        {
            if (_activeRun is not { } run || run.Token != token || run.Completion.Task.IsCompleted)
                return false;
            if (version != ProtocolVersion)
            {
                run.Completion.TrySetResult(Failed("Publish the updated website crawl workflow in n8n before retrying."));
                return false;
            }

            run.Site = site;
            UpdateProgress(run, CrawlStage.CollectingPages);
        }
        OnStatusChanged?.Invoke();
        return true;
    }

    public bool CanAcceptCallback(string token)
    {
        lock (_gate)
            return _activeRun is { Site: not null } run && run.Token == token && !run.Completion.Task.IsCompleted;
    }

    public bool RecordQueue(string token, int total) =>
        RecordActivity(token, CrawlStage.FetchingPages, run =>
        {
            if (total >= 0)
                run.TotalPages = Math.Max(total, run.TotalPages ?? 0);
        });

    public bool RecordDiscovery(string token) => RecordActivity(token, CrawlStage.CollectingPages);

    public bool RecordPageChecked(string token, int pageNumber, bool failed) =>
        RecordActivity(token, CrawlStage.FetchingPages, run =>
        {
            if (pageNumber > 0 && pageNumber <= run.TotalPages && run.CheckedPages.Add(pageNumber) && failed)
                run.FailedPages++;
        });

    public bool RecordPageSaved(string token, string fileName, Action commit) =>
        RecordActivity(token, CrawlStage.SavingPages, run =>
        {
            commit();
            run.SavedPages.Add(fileName);
        });

    public bool RecordIndexSaved(string token, Action commit) => RecordActivity(token, CrawlStage.Finalizing, _ => commit());

    public bool RecordPageDeleted(string token, string fileName, Action commit) =>
        RecordActivity(token, CrawlStage.Finalizing, run =>
        {
            commit();
            run.DeletedPages.Add(fileName);
        });

    public bool RecordReport(string token, string site, string reportPath, Action commit) =>
        RecordActivity(token, CrawlStage.Finalizing, run =>
        {
            commit();
            try
            {
                using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
                var summary = report.RootElement.GetProperty("summary");
                var staleFiles = report.RootElement.GetProperty("staleUrlsDeleted").EnumerateArray()
                    .Select(item => ReadStaleFileName(item, site)).ToHashSet(StringComparer.Ordinal);
                var blocked = summary.TryGetProperty("blockedByRobotsCount", out var count) ? count.GetInt32() : 0;
                var failedSitemaps = summary.TryGetProperty("failedSitemapCount", out var sitemaps) ? sitemaps.GetInt32() : 0;
                var failure = report.RootElement.TryGetProperty("failureCode", out var code) ? code.GetString() : null;
                run.Report = new CrawlReport(site, Path.GetDirectoryName(reportPath)!, summary.GetProperty("failedCount").GetInt32(),
                    summary.GetProperty("unchangedSkippedCount").GetInt32(), blocked, failedSitemaps, staleFiles, failure);
            }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                run.Completion.TrySetResult(FinishWithIssue(run, "The completion report could not be read."));
            }
        });

    private static string ReadStaleFileName(JsonElement item, string site)
    {
        var path = item.GetProperty("fileName").GetString();
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName) || (path != fileName && path != $"raw/{site}/{fileName}"))
            throw new JsonException("Invalid stale page path in crawl report.");

        return fileName;
    }

    private bool RecordActivity(string token, CrawlStage stage, Action<CrawlRun>? update = null)
    {
        lock (_gate)
        {
            if (_activeRun is not { } run || run.Token != token || run.Completion.Task.IsCompleted)
                return false;

            update?.Invoke(run);
            UpdateProgress(run, stage);
            try
            {
                CompleteIfReady(run);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                run.Completion.TrySetResult(Failed("The portal could not check the saved pages. Check workspace storage and retry."));
            }
        }
        OnStatusChanged?.Invoke();
        return true;
    }

    private void UpdateProgress(CrawlRun run, CrawlStage stage)
    {
        if (stage > run.Stage)
            run.Stage = stage;
        run.LastUpdateAt = DateTimeOffset.UtcNow;

        var message = run.Stage switch
        {
            CrawlStage.WaitingForWorkflow => "Waiting for the crawl workflow",
            CrawlStage.CollectingPages => "Discovering and fetching pages",
            CrawlStage.FetchingPages => "Checking queued pages",
            CrawlStage.SavingPages => "Crawling and saving pages",
            CrawlStage.Finalizing => "Finalizing the crawl",
            _ => "Starting the crawl"
        };
        var staleFiles = run.Report?.StaleFiles;
        Status = new CrawlStatus(message, IsRunning: true,
            Progress: new CrawlProgress(run.Stage, run.StartedAt, run.LastUpdateAt, run.SavedPages.Count,
                staleFiles?.Count(run.DeletedPages.Contains) ?? 0, staleFiles?.Count,
                run.TotalPages, run.CheckedPages.Count, run.FailedPages));
    }

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
        if (report.FailureCode == "robots-unavailable")
            status = Failed("The crawler could not verify the site's robots.txt rules.");
        else if (report.FailureCode == "index-write-failed")
            status = FinishWithIssue(run, "The workflow could not update the crawl index.");
        else if (!retained || (saved == 0 && report.Failed > 0) || (saved == 0 && report.BlockedByRobots > 0 && report.Skipped == 0))
        {
            var details = report.Failed > 0 ? $" {failedLabel}." : "";
            var blocked = report.BlockedByRobots > 0 ? $" The site's robots.txt blocked {report.BlockedByRobots} of the candidate URLs." : "";
            status = Failed($"No pages were saved.{details}{blocked} Check the URL and retry the crawl.");
        }
        else if (report.Failed > 0 || report.FailedSitemaps > 0)
        {
            var sitemapWarning = report.FailedSitemaps > 0 ? " Some sitemaps could not be read." : "";
            status = new CrawlStatus($"Ready to ingest with crawl errors. {savedLabel}, {failedLabel}.{sitemapWarning}",
                report.Site, NeedsAttention: true);
        }
        else
        {
            var skipped = report.Skipped > 0 ? $", {report.Skipped} unchanged" : "";
            status = new CrawlStatus($"Ready to ingest. {savedLabel}{skipped}.", report.Site);
        }

        run.Completion.TrySetResult(status);
    }

    private static CrawlStatus FinishWithIssue(CrawlRun run, string message)
    {
        var saved = run.SavedPages.Except(run.DeletedPages).Count();
        return saved > 0
            ? new CrawlStatus($"Partial crawl. {saved} {(saved == 1 ? "page" : "pages")} ready to ingest. {message}",
                run.Site, NeedsAttention: true)
            : Failed(message);
    }

    private static CrawlStatus Failed(string message) => new($"Crawl failed. {message}", NeedsAttention: true);

    public sealed record CrawlStatus(string Message, string? IngestSite = null, bool IsRunning = false,
        bool NeedsAttention = false, CrawlProgress? Progress = null, TimeSpan? Elapsed = null);

    public sealed record CrawlProgress(CrawlStage Stage, DateTimeOffset StartedAt, DateTimeOffset LastUpdateAt,
        int SavedPages, int StalePagesRemoved, int? StalePagesTotal, int? TotalPages, int CheckedPages, int FailedPages);

    public enum CrawlScope { SinglePage, Website }
    public enum CrawlStage { Starting, WaitingForWorkflow, CollectingPages, FetchingPages, SavingPages, Finalizing }

    private sealed class CrawlRun(CancellationTokenSource submission)
    {
        public CancellationTokenSource Submission { get; } = submission;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastUpdateAt { get; set; }
        public CrawlStage Stage { get; set; }
        public string? Token { get; set; }
        public string? Site { get; set; }
        public int? TotalPages { get; set; }
        public HashSet<int> CheckedPages { get; } = [];
        public int FailedPages { get; set; }
        public HashSet<string> SavedPages { get; } = new(StringComparer.Ordinal);
        public HashSet<string> DeletedPages { get; } = new(StringComparer.Ordinal);
        public CrawlReport? Report { get; set; }
        public TaskCompletionSource<CrawlStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record CrawlReport(string Site, string SitePath, int Failed, int Skipped, int BlockedByRobots, int FailedSitemaps,
        HashSet<string> StaleFiles, string? FailureCode);
}
