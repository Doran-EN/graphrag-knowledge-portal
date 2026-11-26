using Microsoft.AspNetCore.Components;
using ObsidianGraphRag.Shared.Models;
using Microsoft.AspNetCore.Components.Forms;
using System.Text.Json;
using ObsidianGraphRag.Shared.Services.Ingestion;
using ObsidianGraphRag.Shared.Services.N8n;
using ObsidianGraphRag.Shared.Services.SiteManager;
using ObsidianGraphRag.Shared.Services.Vault;

namespace ObsidianGraphRag.Shared.Pages;

public partial class IngestPage : IDisposable
{
    [CascadingParameter]
    public WorkspaceScope? Workspace { get; set; }

    [Inject]
    private ObsidianIngestEngine IngestEngine { get; set; } = default!;

    [Inject]
    private SiteManagerService SiteManager { get; set; } = default!;

    [Inject]
    private VaultGraphEngine GraphEngine { get; set; } = default!;

    [Inject]
    private N8nCrawlService N8nService { get; set; } = default!;

    [Inject]
    private DocumentImportService DocumentImport { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "site")]
    public string? SiteQuery { get; set; }

    private const string AllDocuments = "all-documents";
    private const string WebsitePrefix = "website:";
    private const string DocumentPrefix = "document:";
    private string selectedTarget = string.Empty;
    private IReadOnlyList<string> availableSites = [];
    private IReadOnlyList<DocumentImportService.UploadedDocument> uploadedDocuments = [];
    private bool hasDocuments;
    private bool reprocess;

    private bool IsBusy => IngestEngine.CurrentJob.IsRunning || isImportingDocument;
    private bool CrawlControlsBusy => IsBusy || N8nService.IsBusy;
    private bool IngestBlocked => IsBusy || (N8nService.IsBusy && selectedTarget.StartsWith(WebsitePrefix, StringComparison.Ordinal));
    private bool HasValidUrl => Uri.TryCreate(targetWebsiteInput.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && !string.IsNullOrEmpty(uri.Host);
    private DocumentImportService.UploadedDocument? SelectedDocument =>
        uploadedDocuments.FirstOrDefault(file => selectedTarget == DocumentPrefix + file.Id);
    private string? SelectedSite => selectedTarget == AllDocuments || SelectedDocument is not null
        ? DocumentImportService.Collection
        : availableSites.FirstOrDefault(site => selectedTarget == WebsitePrefix + site);
    private string? SelectedLabel => selectedTarget == AllDocuments
        ? "All uploaded documents"
        : SelectedDocument is { } file ? DocumentLabel(file) : SelectedSite;

    private string targetWebsiteInput = string.Empty;
    private bool? isN8nOnline;
    private N8nCrawlService.CrawlStatus? crawlStatus;
    private PeriodicTimer? crawlClock;
    private IBrowserFile? document;
    private bool isImportingDocument;
    private bool documentImportSucceeded;
    private string? documentImportStatus;
    private int uploadInputVersion;

    protected override async Task OnInitializedAsync()
    {
        IngestEngine.OnProgressChanged += RefreshProgress;
        IngestEngine.OnSourceArchiveChanged += RefreshSourceArchive;
        N8nService.OnStatusChanged += RefreshCrawlStatus;

        await RefreshSources();
        ApplyCrawlStatus();
    }

    protected override void OnParametersSet()
    {
        if (SiteQuery == DocumentImportService.Collection && hasDocuments)
            selectedTarget = AllDocuments;
        else if (SiteQuery is not null && availableSites.Contains(SiteQuery))
            selectedTarget = WebsitePrefix + SiteQuery;
    }

    private async Task TriggerN8nCrawl()
    {
        if (CrawlControlsBusy || !HasValidUrl)
            return;

        await N8nService.TriggerCrawlAsync(targetWebsiteInput, N8nCrawlService.CrawlScope.Section);
    }

    private void CancelCrawl(DateTimeOffset startedAt)
    {
        if (Workspace?.CanAccess() != false)
            N8nService.CancelCrawl(startedAt);
    }

    private void RefreshCrawlStatus() => _ = InvokeAsync(() =>
    {
        if (Workspace?.CanAccess() == false)
            return;
        ApplyCrawlStatus();
        StateHasChanged();
    });

    private void ApplyCrawlStatus()
    {
        crawlStatus = N8nService.Status;
        if (crawlStatus is null || crawlStatus.IsRunning)
            return;

        ReadSources();
        if (crawlStatus.IngestSite is { } site && availableSites.Contains(site))
        {
            selectedTarget = WebsitePrefix + site;
            reprocess = false;
        }
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (crawlStatus?.Progress is null)
        {
            StopCrawlClock();
        }
        else if (crawlClock is null)
        {
            crawlClock = new PeriodicTimer(TimeSpan.FromSeconds(1));
            _ = RefreshCrawlClockAsync(crawlClock);
        }
    }

    private async Task RefreshCrawlClockAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync())
            await InvokeAsync(() =>
            {
                if (ReferenceEquals(crawlClock, timer))
                    StateHasChanged();
            });
    }

    private void StopCrawlClock()
    {
        crawlClock?.Dispose();
        crawlClock = null;
    }

    private static string CrawlStageDetail(N8nCrawlService.CrawlProgress progress) => progress.Stage switch
    {
        N8nCrawlService.CrawlStage.Starting => "Waiting for the crawler to accept the website.",
        N8nCrawlService.CrawlStage.WaitingForWorkflow => "n8n accepted the request. Waiting for the workflow to connect to this workspace.",
        N8nCrawlService.CrawlStage.CollectingPages => "Checking crawl rules and discovering pages in the selected scope.",
        N8nCrawlService.CrawlStage.FetchingPages => "Fetching the selected pages.",
        N8nCrawlService.CrawlStage.SavingPages => "Saving pages as they arrive from the crawler.",
        _ when progress.StalePagesTotal is > 0 => $"Removing stale pages. {progress.StalePagesRemoved} of {progress.StalePagesTotal} removed.",
        _ => "Waiting for the final report and any cleanup."
    };

    private static string FormatCrawlTime(TimeSpan duration)
    {
        var elapsed = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
    }

    private async Task RefreshSources()
    {
        ReadSources();
        isN8nOnline = await N8nService.CheckHealthAsync();
    }

    private void ReadSources()
    {
        var sites = SiteManager.GetSiteNames();
        var archive = SourceArchive.Read(GraphEngine.RootPath);
        var documentFolder = Path.Combine(GraphEngine.RootPath, "raw", DocumentImportService.Collection);
        hasDocuments = Directory.Exists(documentFolder) &&
            Directory.EnumerateFiles(documentFolder, "*.md", SearchOption.AllDirectories)
                .Any(file => !archive.ContainsPath(Path.GetRelativePath(GraphEngine.RootPath, file)));
        availableSites = sites.Where(site => site != DocumentImportService.Collection &&
                Directory.EnumerateFiles(Path.Combine(GraphEngine.RootPath, "raw", site), "*.md", SearchOption.AllDirectories).Any())
            .OrderBy(site => site, StringComparer.OrdinalIgnoreCase).ToArray();
        uploadedDocuments = DocumentImport.GetUploadedDocuments();
        if (SelectedSite is null || (selectedTarget == AllDocuments && !hasDocuments))
            selectedTarget = string.Empty;
    }

    private void RefreshSourceArchive() => _ = InvokeAsync(() =>
    {
        if (Workspace?.CanAccess() == false)
            return;
        ReadSources();
        StateHasChanged();
    });

    private string DocumentLabel(DocumentImportService.UploadedDocument file) =>
        uploadedDocuments.Count(other => string.Equals(other.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"{file.FileName} ({file.Id[^Math.Min(file.Id.Length, 8)..]})"
            : file.FileName;

    private void SelectDocument(InputFileChangeEventArgs args)
    {
        document = args.File;
        documentImportStatus = null;
    }

    private async Task ImportDocument()
    {
        if (document is null || IsBusy)
            return;

        isImportingDocument = true;
        documentImportStatus = null;
        documentImportSucceeded = false;
        try
        {
            if (document.Size == 0 || document.Size > DocumentImportService.MaxFileBytes)
                throw new InvalidOperationException("Choose a non-empty file no larger than 20 MB.");

            await using var input = document.OpenReadStream(DocumentImportService.MaxFileBytes);
            var result = await DocumentImport.ImportAsync(document.Name, input);
            if (Workspace?.CanAccess() == false)
                return;
            ReadSources();
            selectedTarget = DocumentPrefix + result.Id;
            reprocess = false;
            documentImportSucceeded = true;
            documentImportStatus = result.AlreadyImported
                ? $"{result.FileName} was already imported and is selected below."
                : $"Converted {result.FileName} into {result.Sections} Markdown sections. It’s selected below for ingestion.";
            document = null;
            uploadInputVersion++;
        }
        catch (InvalidOperationException error)
        {
            documentImportStatus = error.Message;
        }
        catch (OperationCanceledException)
        {
            documentImportStatus = "Document conversion timed out. You can retry the file.";
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        {
            documentImportStatus = "The document could not be converted or saved. Check that Docling is running and try again.";
        }
        finally
        {
            isImportingDocument = false;
        }
    }

    private async Task RunIngestion()
    {
        if (IngestBlocked || SelectedSite is not { } site || SelectedLabel is not { } label)
            return;
        await IngestEngine.RunIngestAsync(site, label, SelectedDocument?.Id, reprocess);
    }

    private void RefreshProgress() => _ = InvokeAsync(() =>
    {
        if (Workspace?.CanAccess() != false)
            StateHasChanged();
    });

    public void Dispose()
    {
        StopCrawlClock();
        IngestEngine.OnProgressChanged -= RefreshProgress;
        IngestEngine.OnSourceArchiveChanged -= RefreshSourceArchive;
        N8nService.OnStatusChanged -= RefreshCrawlStatus;
    }
}
