using ObsidianGraphRag.Shared.Services.N8n;
using ObsidianGraphRag.Shared.Services.SiteManager;

namespace ObsidianGraphRag.Shared.Services.Ingestion;

public sealed class SourceArchiveService(
    ObsidianIngestEngine engine, DocumentImportService documents, SiteManagerService sites,
    N8nCrawlService crawler, Func<Task<bool>>? canManageArchive = null)
{
    public Task<bool> CanManageAsync() => canManageArchive?.Invoke() ?? Task.FromResult(false);

    public async Task SetArchivedAsync(string site, string? documentId, bool archived)
    {
        if (!await CanManageAsync())
            throw new UnauthorizedAccessException("Only the portal owner can manage archived sources.");

        if (site == DocumentImportService.Collection)
        {
            if (!documents.GetUploadedDocuments(includeArchived: true).Any(document => document.Id == documentId))
                throw new InvalidOperationException("This document is no longer available.");
        }
        else
        {
            if (documentId is not null || !sites.GetSiteNames(includeArchived: true).Contains(site, StringComparer.Ordinal))
                throw new InvalidOperationException("This website is no longer available.");
            if (crawler.IsBusy)
                throw new InvalidOperationException("Wait for the website crawl to finish before changing the archive.");
        }

        engine.SetSourceArchived(site, documentId, archived);
    }
}
