using Microsoft.AspNetCore.Components;
using ObsidianGraphRag.Shared.Models;
using ObsidianGraphRag.Shared.Services.Ingestion;
using ObsidianGraphRag.Shared.Services.SiteManager;
using ObsidianGraphRag.Shared.Services.Vault;

namespace ObsidianGraphRag.Shared.Pages;

public partial class DashboardPage
{
    [Inject]
    private SiteManagerService SiteManager { get; set; } = default!;

    [Inject]
    private VaultGraphEngine GraphEngine { get; set; } = default!;

    [Inject]
    private NavigationManager Nav { get; set; } = default!;

    [Inject]
    private SourceArchiveService Archive { get; set; } = default!;

    [Inject]
    private DocumentImportService Documents { get; set; } = default!;

    [CascadingParameter]
    public WorkspaceScope? Workspace { get; set; }

    [CascadingParameter(Name = "PublishWebsite")]
    public RenderFragment<string>? PublishWebsite { get; set; }

    private List<SiteKnowledgeSummary> sites = new();
    private IReadOnlyList<DocumentImportService.UploadedDocument> managedDocuments = [];
    private bool canManageArchive;
    private bool isChangingArchive;
    private bool archiveFailed;
    private ArchiveTarget? pendingArchive;
    private string? archiveMessage;
    private string? archiveMessageSite;

    protected override async Task OnInitializedAsync()
    {
        canManageArchive = await Archive.CanManageAsync();
        RefreshData();
    }

    private void RefreshData()
    {
        GraphEngine.Reload();
        sites = SiteManager.GetAllManagedSites(includeArchived: canManageArchive);
        if (canManageArchive)
            managedDocuments = Documents.GetUploadedDocuments(includeArchived: true);
    }

    private void ConfirmArchive(ArchiveTarget target)
    {
        archiveMessage = null;
        pendingArchive = target;
    }

    private async Task ChangeArchive(ArchiveTarget target, bool archived)
    {
        if (isChangingArchive)
            return;

        pendingArchive = null;
        archiveMessage = null;
        archiveMessageSite = target.Site;
        isChangingArchive = true;
        try
        {
            await Archive.SetArchivedAsync(target.Site, target.DocumentId, archived);
            RefreshData();
            archiveFailed = false;
            archiveMessage = archived ? $"Archived {target.Label}." : $"Restored {target.Label}.";
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            archiveFailed = true;
            archiveMessage = error is InvalidOperationException ? error.Message : "Could not update the archive. Try again.";
        }
        finally
        {
            isChangingArchive = false;
        }
    }

    private string DocumentLabel(DocumentImportService.UploadedDocument file) =>
        managedDocuments.Count(other => string.Equals(other.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"{file.FileName} ({file.Id[^Math.Min(file.Id.Length, 8)..]})"
            : file.FileName;

    private void RefreshVault()
    {
        RefreshData();
        StateHasChanged();
    }

    private void NavigateToSite(string siteName)
    {
        Nav.NavigateTo(WorkspaceScope.Link($"/explorer/{Uri.EscapeDataString(siteName)}", Workspace?.DepartmentId));
    }

    private void NavigateToIngest(string siteName)
    {
        Nav.NavigateTo(WorkspaceScope.Link($"/ingest?site={Uri.EscapeDataString(siteName)}", Workspace?.DepartmentId));
    }

    private sealed record ArchiveTarget(string Site, string? DocumentId, string Label);
}
