using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using ObsidianGraphRag.Shared.Services.Ingestion;
using ObsidianGraphRag.Shared.Services.LLM;
using ObsidianGraphRag.Shared.Services.N8n;
using ObsidianGraphRag.Shared.Services.Retrieval;
using ObsidianGraphRag.Shared.Services.Vault;

namespace obsidian_graphrag_v2.Web.Services;

internal sealed class PortalWorkspaces(PortalAccounts accounts, ILlmClient llm, IHttpClientFactory clients,
    IConfiguration configuration, ILogger<SemanticNoteIndex> logger, CrawlAccess crawlAccess)
{
    private readonly ConcurrentDictionary<string, Lazy<Workspace>> _workspaces = new();

    public Workspace For(string id) => _workspaces.GetOrAdd(id, key => new Lazy<Workspace>(() =>
    {
        var path = accounts.WorkspacePath(key);
        Directory.CreateDirectory(path);
        var graph = new VaultGraphEngine(path);
        return new Workspace(graph.RootPath, new ObsidianIngestEngine(graph, llm),
            new SemanticNoteIndex(graph, llm, logger),
            new N8nCrawlService(clients.CreateClient(), configuration, url => crawlAccess.Create(key, url)));
    })).Value;

    public sealed record Workspace(string Path, ObsidianIngestEngine Ingest, SemanticNoteIndex Search, N8nCrawlService Crawl);
}

internal sealed class PortalAuthenticationState(ILoggerFactory logger, PortalAccounts accounts)
    : RevalidatingServerAuthenticationStateProvider(logger)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(10);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken) =>
        Task.FromResult(accounts.GetWorkspaceId(state.User) is not null);
}

internal sealed class PortalWorkspaceSelection(NavigationManager navigation)
{
    private readonly Lazy<string?> _departmentId = new(() => FromUri(navigation.Uri));
    public string? DepartmentId => _departmentId.Value;

    public static string? FromUri(string uri)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri).Query);
        var value = query.TryGetValue("department", out var departments) ? departments.FirstOrDefault() : null;
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

internal sealed class PortalCircuitAccess(AuthenticationStateProvider authentication, PortalAccounts accounts,
    PortalWorkspaceSelection selection) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) => async activity =>
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (accounts.GetWorkspaceId(state.User, selection.DepartmentId) is not null)
            await next(activity);
    };
}

internal sealed class CrawlAccess(IDataProtectionProvider protection, PortalAccounts accounts)
{
    // Callbacks also require the token to match a running crawl in the workspace.
    private readonly IDataProtector _protector = protection.CreateProtector("GraphRAG.PrivateCrawl.v2");

    public string Create(string id, string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort ||
            uri.HostNameType != UriHostNameType.Dns || !uri.IdnHost.Contains('.') ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Enter a public website URL using HTTP or HTTPS.");
        var site = uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.IdnHost[4..] : uri.IdnHost;
        site = site.Split('.')[0].ToLowerInvariant();
        if (site == "documents" || !System.Text.RegularExpressions.Regex.IsMatch(site, @"\A[A-Za-z0-9_-]{1,63}\z"))
            throw new ArgumentException("This website cannot be used as a crawl source.");
        if (SourceArchive.IsWebsiteArchived(accounts.WorkspacePath(id), site))
            throw new ArgumentException("This website is archived. Restore it under Managed Websites before crawling again.");
        return _protector.Protect($"{id}\n{site}\n{url}");
    }

    public CrawlGrant? Read(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096)
            return null;

        try
        {
            var fields = _protector.Unprotect(token).Split('\n', 3);
            var workspace = accounts.WorkspacePath(fields[0]);
            return SourceArchive.IsWebsiteArchived(workspace, fields[1])
                ? null
                : new CrawlGrant(fields[0], workspace, fields[1], fields[2], token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public sealed record CrawlGrant(string WorkspaceId, string Path, string Site, string Url, string Token);
}
