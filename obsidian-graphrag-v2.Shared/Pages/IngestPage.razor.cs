using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using System.Text.Json;
using ObsidianGraphRag.Shared.Services.Ingestion;
using ObsidianGraphRag.Shared.Services.N8n;
using ObsidianGraphRag.Shared.Services.SiteManager;
using ObsidianGraphRag.Shared.Services.Vault;

namespace ObsidianGraphRag.Shared.Pages;

public partial class IngestPage : IDisposable
{
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
    private IBrowserFile? document;
    private bool isImportingDocument;
    private bool documentImportSucceeded;
    private string? documentImportStatus;
    private int uploadInputVersion;

    protected override async Task OnInitializedAsync()
    {
        IngestEngine.OnProgressChanged += RefreshProgress;
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

        await N8nService.TriggerCrawlAsync(targetWebsiteInput);
    }

    private void RefreshCrawlStatus() => _ = InvokeAsync(() =>
    {
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

    private async Task RefreshSources()
    {
        ReadSources();
        isN8nOnline = await N8nService.CheckHealthAsync();
    }

    private void ReadSources()
    {
        var sites = SiteManager.GetSiteNames();
        var documentFolder = Path.Combine(GraphEngine.RootPath, "raw", DocumentImportService.Collection);
        hasDocuments = sites.Contains(DocumentImportService.Collection) &&
            Directory.EnumerateFiles(documentFolder, "*.md", SearchOption.AllDirectories).Any();
        availableSites = sites.Where(site => site != DocumentImportService.Collection &&
                Directory.EnumerateFiles(Path.Combine(GraphEngine.RootPath, "raw", site), "*.md", SearchOption.AllDirectories).Any())
            .OrderBy(site => site, StringComparer.OrdinalIgnoreCase).ToArray();
        uploadedDocuments = DocumentImport.GetUploadedDocuments();
        if (SelectedSite is null || (selectedTarget == AllDocuments && !hasDocuments))
            selectedTarget = string.Empty;
    }

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

    private void RefreshProgress() => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        IngestEngine.OnProgressChanged -= RefreshProgress;
        N8nService.OnStatusChanged -= RefreshCrawlStatus;
    }
}
