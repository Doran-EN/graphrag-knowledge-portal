using System.Security.Claims;
using System.Text.RegularExpressions;
using ObsidianGraphRag.Shared.Services.Ingestion;

namespace obsidian_graphrag_v2.Web.Services;

internal sealed class PortalWebsitePublisher(PortalAccounts accounts, PortalWorkspaces workspaces)
{
    public async Task PublishAsync(ClaimsPrincipal owner, string site, string departmentId)
    {
        if (!owner.IsInRole("Owner") || accounts.GetWorkspaceId(owner) is not { } sourceId ||
            string.IsNullOrEmpty(departmentId) || accounts.GetWorkspaceId(owner, departmentId) is not { } destinationId)
            throw new UnauthorizedAccessException();

        if (!Regex.IsMatch(site, @"\A[A-Za-z0-9_-]{1,63}\z") ||
            site.Equals(DocumentImportService.Collection, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a website crawl to publish.");

        var source = workspaces.For(sourceId);
        var destination = workspaces.For(destinationId);
        source.Crawl.RunWhileIdle(() => destination.Crawl.RunWhileIdle(() =>
        {
            if (destination.Ingest.CurrentJob.IsRunning)
                throw new InvalidOperationException("Wait for the department's current ingestion to finish.");

            CopyPages(source.Path, destination.Path, site);
        }));
        await destination.Ingest.RunIngestAsync(site, site);
    }

    private static void CopyPages(string sourceRoot, string destinationRoot, string site)
    {
        var source = Path.Combine(sourceRoot, "raw", site);
        if (!Directory.Exists(source) || SourceArchive.IsWebsiteArchived(sourceRoot, site))
            throw new InvalidOperationException("Only an active website crawl can be published.");

        var targetRoot = Path.Combine(destinationRoot, "raw");
        var target = Path.Combine(targetRoot, site);
        if (Directory.Exists(target))
            throw new InvalidOperationException("This website already exists in the department workspace.");

        var pages = Directory.GetFiles(source, "*.md");
        if (pages.Length == 0)
            throw new InvalidOperationException("This crawl has no saved pages to publish.");

        // Stage the pages outside raw so readers only see the completed copy.
        var staging = Path.Combine(destinationRoot, ".imports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var page in pages)
                File.Copy(page, Path.Combine(staging, Path.GetFileName(page)));

            if (SourceArchive.IsWebsiteArchived(sourceRoot, site))
                throw new InvalidOperationException("This website was archived before it could be published.");

            Directory.CreateDirectory(targetRoot);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }
}
