using ObsidianGraphRag.Shared.Models;
using ObsidianGraphRag.Shared.Services.Ingestion;
using ObsidianGraphRag.Shared.Services.Vault;

namespace ObsidianGraphRag.Shared.Services.Retrieval;

internal static class CrossDocumentEvidence
{
    internal sealed record Match(ObsidianNote Note, string[] BridgeTerms);

    internal static Match? Find(VaultGraphEngine graph, List<ObsidianNote> scopedNotes,
        List<ObsidianNote> selected, Func<ObsidianNote, int> coverage,
        IReadOnlyDictionary<ObsidianNote, float> semanticScores)
    {
        var scope = scopedNotes.ToHashSet();
        var selectedSources = selected.Select(SourceIdentity).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<ObsidianNote, string[]>();

        foreach (var seed in selected)
        {
            foreach (var neighbor in Neighbors(graph, seed))
            {
                if (scope.Contains(neighbor))
                    Consider(neighbor, []);
                if (neighbor.NoteType is not (TheSchemaDefinitions.TypeEntity or TheSchemaDefinitions.TypeConcept))
                    continue;

                // Shared entities can connect in-scope sources without adding the entity's mixed text.
                var bridgeTerms = neighbor.Aliases.Prepend(neighbor.Title).Where(term => term.Length >= 3).ToArray();
                foreach (var linked in Neighbors(graph, neighbor).Where(scope.Contains))
                    Consider(linked, bridgeTerms);
            }
        }

        return candidates
            .Select(candidate => (Note: candidate.Key, BridgeTerms: candidate.Value, Coverage: coverage(candidate.Key),
                Similarity: semanticScores.GetValueOrDefault(candidate.Key)))
            .Where(candidate => candidate.Coverage > 0 || candidate.Similarity > 0)
            .OrderByDescending(candidate => candidate.Coverage)
            .ThenByDescending(candidate => candidate.Similarity)
            .ThenBy(candidate => candidate.BridgeTerms.Length > 0)
            .ThenBy(candidate => candidate.Note.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new Match(candidate.Note, candidate.BridgeTerms))
            .FirstOrDefault();

        void Consider(ObsidianNote note, string[] bridgeTerms)
        {
            if (SourceIdentity(note) is not { } identity || selectedSources.Contains(identity))
                return;

            // Prefer the original passage when a link points to a compiled summary.
            var original = ResolvePath(graph, OriginalPath(note));
            if (original is not null && scope.Contains(original))
                note = original;

            if (!candidates.TryGetValue(note, out var existing) || (existing.Length > 0 && bridgeTerms.Length == 0))
                candidates[note] = bridgeTerms;
        }
    }

    private static IEnumerable<ObsidianNote> Neighbors(VaultGraphEngine graph, ObsidianNote note)
    {
        foreach (var neighbor in graph.Get1HopNeighbors(note.RelativePath))
            yield return neighbor;

        if (note.NoteType == TheSchemaDefinitions.TypeRaw)
        {
            var rawSubpath = note.RelativePath["raw/".Length..];
            var separator = rawSubpath.IndexOf('/');
            if (separator < 0)
                yield break;

            var compiledPath = $"wiki/sources/{rawSubpath[..(separator + 1)]}" +
                ObsidianIngestEngine.GetWikiSourceSubpath(rawSubpath[(separator + 1)..] + ".md");
            if (ResolvePath(graph, compiledPath) is { } compiled)
            {
                foreach (var neighbor in graph.Get1HopNeighbors(compiled.RelativePath))
                    yield return neighbor;
            }
        }
        else
        {
            foreach (var path in note.Sources)
            {
                if (ResolvePath(graph, path) is { NoteType: TheSchemaDefinitions.TypeRaw } source)
                    yield return source;
            }
        }
    }

    private static ObsidianNote? ResolvePath(VaultGraphEngine graph, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var key = path.Replace('\\', '/');
        if (key.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            key = key[..^3];
        return graph.Notes.GetValueOrDefault(key);
    }

    private static string? OriginalPath(ObsidianNote note) => note.NoteType switch
    {
        TheSchemaDefinitions.TypeRaw => note.RelativePath,
        TheSchemaDefinitions.TypeSource => note.Frontmatter.GetValueOrDefault("source_path")?.ToString(),
        _ => null
    };

    internal static string? SourceIdentity(ObsidianNote note)
    {
        var path = OriginalPath(note)?.Replace('\\', '/');
        if (path is null)
            return null;

        var documentPrefix = $"raw/{DocumentImportService.Collection}/";
        if (path.StartsWith(documentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var endOfDocumentId = path.IndexOf('/', documentPrefix.Length);
            if (endOfDocumentId >= 0)
                return path[..endOfDocumentId];
        }

        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
    }
}
