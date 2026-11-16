using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ObsidianGraphRag.Shared.Models
{
    public static class KnowledgeRelationTypes
    {
        public const string SameAs = "SAME_AS";
        public const string SubconceptOf = "SUBCONCEPT_OF";
        public const string Document = "DOCUMENT";
        public const string RelatedTo = "RELATED_TO";
        public const string Distinct = "DISTINCT";
    }

    public class KnowledgeNodeCandidate
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = "entity"; // "entity" or "concept"
        public string EntityType { get; set; } = "General"; // Organization, Person, Product, Service, Document, etc.
        public List<string> Aliases { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
        public string RelationType { get; set; } = KnowledgeRelationTypes.Distinct; // SAME_AS, SUBCONCEPT_OF, RELATED_TO, DISTINCT
        public string? TargetCanonicalName { get; set; }
        public List<string> ParentConcepts { get; set; } = new();
        public List<string> RelatedEntities { get; set; } = new();
        public string? PublishedBy { get; set; }
    }

    public class EntityCandidate
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "General";
        public List<string> Aliases { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public string Relations { get; set; } = string.Empty;
        public List<string> Evidence { get; set; } = new();
        public string? OwnerSite { get; set; }
    }

    public class ConceptCandidate
    {
        public string Name { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
        public string Details { get; set; } = string.Empty;
        public List<string> Evidence { get; set; } = new();
    }

    public class SourceSummaryDto
    {
        public string Slug { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string> KeyPoints { get; set; } = new();
        public List<string> RelatedLinks { get; set; } = new();
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string? RejectionReason { get; set; }
        public string CleanedName { get; set; } = string.Empty;

        public static ValidationResult Pass(string cleanedName) => new() { IsValid = true, CleanedName = cleanedName };
        public static ValidationResult Reject(string reason) => new() { IsValid = false, RejectionReason = reason };
    }

    public class KnowledgeCatalogEntry
    {
        public string Title { get; set; } = string.Empty;
        public string NormalizedTitle { get; set; } = string.Empty;
        public string Kind { get; set; } = "entity"; // "entity" | "concept"
        public string? EntityType { get; set; }
        public List<string> Aliases { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public List<string> Sources { get; set; } = new();
        public string FilePath { get; set; } = string.Empty;
    }

    public class KnowledgeCatalog
    {
        private readonly Dictionary<string, KnowledgeCatalogEntry> _canonicalMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, KnowledgeCatalogEntry> _normalizedMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, KnowledgeCatalogEntry> _aliasMap = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _canonicalMap.Count;

        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var lower = name.Trim().ToLowerInvariant();
            lower = Regex.Replace(lower, @"[_\-\s]+", " ");
            return lower.Trim();
        }

        public void Register(KnowledgeCatalogEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Title)) return;
            entry.NormalizedTitle = Normalize(entry.Title);

            _canonicalMap[entry.Title] = entry;
            _normalizedMap[entry.NormalizedTitle] = entry;

            foreach (var alias in entry.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    var normAlias = Normalize(alias);
                    if (!string.IsNullOrEmpty(normAlias))
                    {
                        _aliasMap[normAlias] = entry;
                    }
                }
            }
        }

        public KnowledgeCatalogEntry? FindMatch(string candidateName)
        {
            if (string.IsNullOrWhiteSpace(candidateName)) return null;

            // 1. Exact canonical match
            if (_canonicalMap.TryGetValue(candidateName, out var canonicalMatch)) return canonicalMatch;

            // 2. Normalized match
            var norm = Normalize(candidateName);
            if (string.IsNullOrEmpty(norm)) return null;

            if (_normalizedMap.TryGetValue(norm, out var normMatch)) return normMatch;

            // 3. Alias match
            if (_aliasMap.TryGetValue(norm, out var aliasMatch)) return aliasMatch;

            return null;
        }

        public void AddAliasToEntry(string canonicalTitle, string newAlias)
        {
            if (_canonicalMap.TryGetValue(canonicalTitle, out var entry))
            {
                if (!entry.Aliases.Contains(newAlias, StringComparer.OrdinalIgnoreCase))
                {
                    entry.Aliases.Add(newAlias);
                    var norm = Normalize(newAlias);
                    if (!string.IsNullOrEmpty(norm))
                        _aliasMap[norm] = entry;
                }
            }
        }

        public List<object> GetRichCatalogContext(int maxItems = 120)
        {
            return _canonicalMap.Values
                .Take(maxItems)
                .Select(e => new
                {
                    title = e.Title,
                    kind = e.Kind,
                    entity_type = e.EntityType,
                    aliases = e.Aliases,
                    summary = e.Summary
                })
                .Cast<object>()
                .ToList();
        }
    }

    // Compatibility wrappers for existing code
    public class EntityRegistryEntry
    {
        public string CanonicalName { get; set; } = string.Empty;
        public string NormalizedName { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
        public string Type { get; set; } = "General";
        public List<string> Sources { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
    }

    public class EntityRegistry : KnowledgeCatalog
    {
        public void Register(EntityRegistryEntry entry)
        {
            Register(new KnowledgeCatalogEntry
            {
                Title = entry.CanonicalName,
                Kind = "entity",
                EntityType = entry.Type,
                Aliases = entry.Aliases,
                Summary = entry.Summary,
                Sources = entry.Sources,
                FilePath = entry.FilePath
            });
        }
    }

    public class IngestionRunContext
    {
        public string SchemaHash { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = "1.0";
        public string VaultPath { get; set; } = string.Empty;
        public string SiteName { get; set; } = string.Empty;
        public bool ForceFull { get; set; }
        public KnowledgeCatalog Catalog { get; } = new();
        public string SchemaPromptContext { get; set; } = string.Empty;
    }
}

