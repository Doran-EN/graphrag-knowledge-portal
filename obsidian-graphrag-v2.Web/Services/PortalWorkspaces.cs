using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
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
        var graph = new VaultGraphEngine(accounts.WorkspacePath(key));
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

internal sealed class PortalCircuitAccess(AuthenticationStateProvider authentication, PortalAccounts accounts) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) => async activity =>
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (accounts.GetWorkspaceId(state.User) is not null)
            await next(activity);
    };
}

internal sealed class CrawlAccess(IDataProtectionProvider protection, PortalAccounts accounts)
{
    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("GraphRAG.PrivateCrawl.v1").ToTimeLimitedDataProtector();

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
        return _protector.Protect($"{id}\n{site}\n{url}", TimeSpan.FromHours(2));
    }

    public (string Path, string Site, string Url)? Read(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096)
            return null;

        try
        {
            var fields = _protector.Unprotect(token).Split('\n', 3);
            return (accounts.WorkspacePath(fields[0]), fields[1], fields[2]);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
