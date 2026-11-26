using ObsidianGraphRag.Shared.Models;

namespace ObsidianGraphRag.Shared.Services.Ingestion;

public sealed class SourceArchive
{
    private const string Marker = ".archived";
    private readonly HashSet<string> documentIds;
    private readonly HashSet<string> siteNames;

    private SourceArchive(IEnumerable<string> ids, IEnumerable<string> sites)
    {
        documentIds = new(ids, StringComparer.OrdinalIgnoreCase);
        siteNames = new(sites, StringComparer.OrdinalIgnoreCase);
    }

    internal bool HasSources => documentIds.Count > 0 || siteNames.Count > 0;

    internal static SourceArchive Read(string root)
    {
        var raw = Path.Combine(root, "raw");
        return new(ArchivedNames(Path.Combine(raw, DocumentImportService.Collection)),
            ArchivedNames(raw).Where(site => !site.Equals(DocumentImportService.Collection, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> ArchivedNames(string root) => Directory.Exists(root)
        ? Directory.EnumerateDirectories(root).Where(IsArchived).Select(Path.GetFileName).OfType<string>()
        : [];

    internal static bool IsArchived(string directory) => File.Exists(Path.Combine(directory, Marker));
    public static bool IsWebsiteArchived(string root, string site) => IsArchived(Path.Combine(root, "raw", site));
    internal bool ContainsWebsite(string site) => siteNames.Contains(site);

    internal static void SetArchived(string directory, bool archived)
    {
        if (!Directory.Exists(directory))
            throw new InvalidOperationException("This source is no longer available.");

        var path = Path.Combine(directory, Marker);
        if (archived)
        {
            using var marker = File.Open(path, FileMode.OpenOrCreate, FileAccess.Write);
        }
        else
        {
            File.Delete(path);
        }
    }

    internal bool ContainsPath(string path)
    {
        var parts = path.Replace('\\', '/').Trim().Split('/');
        var collection = parts.Length > 0 && parts[0].Equals("raw", StringComparison.OrdinalIgnoreCase) ? 1
            : parts.Length > 1 && parts[0].Equals("wiki", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("sources", StringComparison.OrdinalIgnoreCase) ? 2
            : parts.Length > 0 && parts[0].Equals("sources", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        if (collection < 0 || parts.Length <= collection)
            return false;

        var site = parts[collection];
        return site.Equals(DocumentImportService.Collection, StringComparison.OrdinalIgnoreCase)
            ? parts.Length > collection + 2 && documentIds.Contains(parts[collection + 1])
            : siteNames.Contains(site);
    }

    internal bool Excludes(ObsidianNote note) =>
        ContainsPath(note.RelativePath) || note.Sources.Any(ContainsPath) ||
        note.Frontmatter.GetValueOrDefault("source_path") is string source && ContainsPath(source);
}
