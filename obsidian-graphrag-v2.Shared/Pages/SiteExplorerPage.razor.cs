using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ObsidianGraphRag.Shared.Models;
using ObsidianGraphRag.Shared.Services.SiteManager;
using ObsidianGraphRag.Shared.Services.Vault;

namespace ObsidianGraphRag.Shared.Pages;

public partial class SiteExplorerPage : IDisposable
{
    [Inject]
    private SiteManagerService SiteManager { get; set; } = default!;

    [Inject]
    private VaultGraphEngine GraphEngine { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Parameter]
    public string SiteName { get; set; } = string.Empty;

    private SiteFileTreeNode? rootTreeNode;
    private ObsidianNote? selectedNote;
    private string selectedNoteKey = "";
    private string selectedNoteGroup = "";
    private string searchTerm = "";
    private DotNetObjectReference<SiteExplorerPage>? dotNetRef;

    private List<string> availableSites = new();
    private string currentScope = string.Empty;
    private List<GraphGroupDto> currentGroups = new();
    private string activeGroupFilter = "all";

    // Modes & States
    private string colorMode = "gray"; // "gray" (Obsidian Monochrome) or "type" or "group"
    private bool pinOnDrag = false;

    // Physics Settings
    private bool showPhysicsPanel = false;
    private int repulsionForce = -130;
    private int linkDistance = 58;
    private double gravityForce = 0.08;

    // Type Filters
    private bool filterEntities = true;
    private bool filterConcepts = true;
    private bool filterSources = true;
    private bool filterRaw = true;

    protected override void OnInitialized()
    {
        GraphEngine.Reload();
        availableSites = GraphEngine.DetectAllVaultSites();
        currentScope = string.IsNullOrWhiteSpace(SiteName) ? (availableSites.FirstOrDefault() ?? string.Empty) : SiteName;
        if (!string.IsNullOrEmpty(currentScope))
        {
            rootTreeNode = SiteManager.GetSiteFileTree(currentScope);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            dotNetRef = DotNetObjectReference.Create(this);
            await LoadGraph();
        }
    }

    private async Task LoadGraph()
    {
        var graphData = GraphEngine.ExportGraphData(currentScope);
        currentGroups = graphData.Groups;
        await JS.InvokeVoidAsync("initGraphView", "hyper-graph-container", graphData, dotNetRef);
    }

    private async Task ReloadGraph()
    {
        GraphEngine.Reload();
        availableSites = GraphEngine.DetectAllVaultSites();
        if (currentScope != "all")
        {
            rootTreeNode = SiteManager.GetSiteFileTree(currentScope);
        }
        await LoadGraph();
    }

    private async Task ChangeScope(string scope)
    {
        currentScope = scope;
        activeGroupFilter = "all";
        if (scope != "all")
        {
            rootTreeNode = SiteManager.GetSiteFileTree(scope);
        }
        await LoadGraph();
    }

    private async Task SetColorMode(string mode)
    {
        colorMode = mode;
        await JS.InvokeVoidAsync("setGraphColorMode", colorMode);
    }

    private async Task TogglePinOnDrag()
    {
        pinOnDrag = !pinOnDrag;
        await JS.InvokeVoidAsync("setPinOnDrag", pinOnDrag);
    }

    private async Task UnpinAllNodes()
    {
        await JS.InvokeVoidAsync("unpinAllNodes");
    }

    private async Task ToggleGroupFilter(string groupName)
    {
        if (string.Equals(activeGroupFilter, groupName, StringComparison.OrdinalIgnoreCase))
        {
            activeGroupFilter = "all";
        }
        else
        {
            activeGroupFilter = groupName;
        }
        await JS.InvokeVoidAsync("filterGraphGroup", activeGroupFilter);
    }

    private async Task ReHeatGraph()
    {
        await JS.InvokeVoidAsync("updateGraphPhysics", repulsionForce, linkDistance, gravityForce);
    }

    private async Task ApplyPhysicsSettings()
    {
        await JS.InvokeVoidAsync("updateGraphPhysics", repulsionForce, linkDistance, gravityForce);
    }

    private void TogglePhysicsPanel()
    {
        showPhysicsPanel = !showPhysicsPanel;
    }

    private async Task ToggleEntityFilter()
    {
        filterEntities = !filterEntities;
        await ApplyFilterAsync();
    }

    private async Task ToggleConceptFilter()
    {
        filterConcepts = !filterConcepts;
        await ApplyFilterAsync();
    }

    private async Task ToggleSourceFilter()
    {
        filterSources = !filterSources;
        await ApplyFilterAsync();
    }

    private async Task ToggleRawFilter()
    {
        filterRaw = !filterRaw;
        await ApplyFilterAsync();
    }

    private async Task ApplyFilterAsync()
    {
        var filterObj = new Dictionary<string, bool>
        {
            { "entity", filterEntities },
            { "concept", filterConcepts },
            { "source", filterSources },
            { "raw", filterRaw }
        };
        await JS.InvokeVoidAsync("filterGraphType", filterObj);
    }

    private async Task SelectFileAndFlyToGraph(string path)
    {
        var key = path.ToLower();
        if (key.EndsWith(".md")) key = key[..^3];
        selectedNoteKey = key;
        if (GraphEngine.Notes.TryGetValue(key, out var note))
        {
            selectedNote = note;
            selectedNoteGroup = GraphEngine.ResolveNodeGroup(note, availableSites);
        }

        await JS.InvokeVoidAsync("focusGraphNode", key);
    }

    private void ToggleFolder(SiteFileTreeNode folder)
    {
        folder.IsExpanded = !folder.IsExpanded;
    }

    private async Task FilterTreeAndGraph()
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var firstMatch = GraphEngine.Notes.Values.FirstOrDefault(n => n.NoteName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
            if (firstMatch != null)
            {
                await JS.InvokeVoidAsync("focusGraphNode", firstMatch.RelativePath.ToLower());
            }
        }
    }

    [JSInvokable]
    public void OnNodeSelected(string id, string label, string type, string summary)
    {
        if (GraphEngine.Notes.TryGetValue(id.ToLower(), out var note))
        {
            selectedNote = note;
            selectedNoteKey = id.ToLower();
            selectedNoteGroup = GraphEngine.ResolveNodeGroup(note, availableSites);
            StateHasChanged();
        }
    }

    [JSInvokable]
    public void OnNodeDeselected()
    {
        selectedNote = null;
        selectedNoteKey = "";
        selectedNoteGroup = "";
        StateHasChanged();
    }

    private async Task DeselectNodeAndResetFocusAsync()
    {
        selectedNote = null;
        selectedNoteKey = "";
        selectedNoteGroup = "";
        await JS.InvokeVoidAsync("resetGraphSelection", false);
    }

    private async Task ResetViewAsync()
    {
        await JS.InvokeVoidAsync("resetGraphView");
    }

    private string GetGroupColor(string groupName)
    {
        var grp = currentGroups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        return grp?.Color ?? "#38bdf8";
    }

    public void Dispose()
    {
        dotNetRef?.Dispose();
    }
}
