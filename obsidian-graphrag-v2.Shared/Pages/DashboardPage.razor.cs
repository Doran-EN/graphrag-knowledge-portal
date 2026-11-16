using Microsoft.AspNetCore.Components;
using ObsidianGraphRag.Shared.Models;
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

    private List<SiteKnowledgeSummary> sites = new();

    protected override void OnInitialized()
    {
        RefreshData();
    }

    private void RefreshData()
    {
        GraphEngine.Reload();
        sites = SiteManager.GetAllManagedSites();
    }

    private void RefreshVault()
    {
        RefreshData();
        StateHasChanged();
    }

    private void NavigateToSite(string siteName)
    {
        Nav.NavigateTo($"/explorer/{siteName}");
    }

    private void NavigateToIngest(string siteName)
    {
        Nav.NavigateTo($"/ingest?site={siteName}");
    }
}
