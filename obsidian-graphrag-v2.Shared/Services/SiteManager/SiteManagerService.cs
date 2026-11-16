using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ObsidianGraphRag.Shared.Models;
using ObsidianGraphRag.Shared.Services.Ingestion;
using ObsidianGraphRag.Shared.Services.Vault;

namespace ObsidianGraphRag.Shared.Services.SiteManager
{
    public class SiteManagerService
    {
        private readonly VaultGraphEngine _graphEngine;

        public SiteManagerService(VaultGraphEngine graphEngine)
        {
            _graphEngine = graphEngine;
        }

        public List<string> GetSiteNames()
        {
            var rawDir = Path.Combine(_graphEngine.RootPath, "raw");
            if (!Directory.Exists(rawDir))
                return new List<string>();

            return Directory.GetDirectories(rawDir)
                .Select(Path.GetFileName)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList()!;
        }

        public List<SiteKnowledgeSummary> GetAllManagedSites()
        {
            var vaultPath = _graphEngine.RootPath;
            var list = new List<SiteKnowledgeSummary>();

            var rawDir = Path.Combine(vaultPath, "raw");
            if (!Directory.Exists(rawDir))
                return list;

            var siteDirs = Directory.GetDirectories(rawDir);
            foreach (var sDir in siteDirs)
            {
                var sName = Path.GetFileName(sDir);
                if (string.IsNullOrEmpty(sName)) continue;

                var rawFiles = Directory.GetFiles(sDir, "*.md", SearchOption.AllDirectories);

                // Discover accurate baseUrl from _crawl_report.json or _crawl_index.json or raw markdown
                string baseUrl = sName == DocumentImportService.Collection ? "" : ResolveSiteUrl(sDir, rawFiles, sName);

                var wikiSourceDir = Path.Combine(vaultPath, "wiki", "sources", sName);
                int sourcesCount = rawFiles
                    .Select(rawFile => ObsidianIngestEngine.GetWikiSourceSubpath(Path.GetRelativePath(sDir, rawFile)))
                    .Where(path => !path.Equals("log.md", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.Ordinal)
                    .Count(path => File.Exists(Path.Combine(wikiSourceDir, path)));

                // Match entities & concepts
                var siteEntities = _graphEngine.Notes.Values
                    .Where(n => n.NoteType == TheSchemaDefinitions.TypeEntity &&
                               (n.Sources.Any(s => s.Contains(sName, StringComparison.OrdinalIgnoreCase)) ||
                                _graphEngine.Get1HopNeighbors(n.RelativePath).Any(nbr => nbr.RelativePath.Contains(sName, StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                var siteConcepts = _graphEngine.Notes.Values
                    .Where(n => n.NoteType == TheSchemaDefinitions.TypeConcept &&
                               (n.Sources.Any(s => s.Contains(sName, StringComparison.OrdinalIgnoreCase)) ||
                                _graphEngine.Get1HopNeighbors(n.RelativePath).Any(nbr => nbr.RelativePath.Contains(sName, StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                int entityCount = siteEntities.Count;
                int conceptCount = siteConcepts.Count;
                int rawCount = rawFiles.Length;

                string syncStatus;
                if (rawCount == 0)
                {
                    syncStatus = "No Documents";
                }
                else if (sourcesCount == 0)
                {
                    syncStatus = "Pending Ingestion";
                }
                else if (sourcesCount < rawCount)
                {
                    syncStatus = $"Partial ({sourcesCount}/{rawCount})";
                }
                else
                {
                    syncStatus = "Synced";
                }

                int healthScore;
                if (sourcesCount == 0)
                {
                    healthScore = rawCount > 0 ? 50 : 20;
                }
                else if (sourcesCount >= rawCount)
                {
                    healthScore = 98;
                }
                else
                {
                    healthScore = 60 + (int)(38.0 * sourcesCount / Math.Max(1, rawCount));
                }

                list.Add(new SiteKnowledgeSummary
                {
                    SiteName = sName,
                    BaseUrl = baseUrl,
                    RawDocCount = rawCount,
                    WikiSourceCount = sourcesCount,
                    EntityCount = entityCount,
                    ConceptCount = conceptCount,
                    TotalEdges = entityCount + conceptCount + sourcesCount,
                    HealthScore = healthScore,
                    SyncStatus = syncStatus
                });
            }

            return list;
        }

        private static string ResolveSiteUrl(string sDir, string[] rawFiles, string sName)
        {
            var reportPath = Path.Combine(sDir, "_crawl_report.json");
            if (File.Exists(reportPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
                    if (doc.RootElement.TryGetProperty("targetUrl", out var tu))
                    {
                        var url = tu.GetString();
                        if (!string.IsNullOrEmpty(url)) return url.TrimEnd('/');
                    }
                }
                catch { }
            }

            var indexPath = Path.Combine(sDir, "_crawl_index.json");
            if (File.Exists(indexPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                    if (doc.RootElement.TryGetProperty("targetUrl", out var tu))
                    {
                        var url = tu.GetString();
                        if (!string.IsNullOrEmpty(url)) return url.TrimEnd('/');
                    }
                }
                catch { }
            }

            // Fallback: peek first raw file
            if (rawFiles.Length > 0)
            {
                try
                {
                    var lines = File.ReadLines(rawFiles[0]).Take(15);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("url:", StringComparison.OrdinalIgnoreCase) ||
                            line.StartsWith("source:", StringComparison.OrdinalIgnoreCase) ||
                            line.StartsWith("source_url:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length == 2)
                            {
                                var rawUrl = parts[1].Trim().Trim('\"', '\'');
                                if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
                                {
                                    return $"{uri.Scheme}://{uri.Authority}";
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return $"https://{sName}.com";
        }

        public SiteFileTreeNode GetSiteFileTree(string siteName)
        {
            var vaultPath = _graphEngine.RootPath;
            var root = new SiteFileTreeNode { Name = siteName, Path = siteName, Type = "folder" };

            // 1. Raw Node
            var rawNode = new SiteFileTreeNode { Name = "raw", Path = $"raw/{siteName}", Type = "folder" };
            var rawSitePath = Path.Combine(vaultPath, "raw", siteName);
            if (Directory.Exists(rawSitePath))
            {
                foreach (var f in Directory.GetFiles(rawSitePath, "*.md"))
                {
                    rawNode.Children.Add(new SiteFileTreeNode
                    {
                        Name = Path.GetFileName(f),
                        Path = Path.GetRelativePath(vaultPath, f).Replace('\\', '/'),
                        Type = "file",
                        NoteType = "raw"
                    });
                }
            }
            root.Children.Add(rawNode);

            // 2. Wiki Node
            var wikiNode = new SiteFileTreeNode { Name = "wiki", Path = "wiki", Type = "folder" };
            foreach (var sub in new[] { "sources", "entities", "concepts" })
            {
                var subNode = new SiteFileTreeNode { Name = sub, Path = $"wiki/{sub}", Type = "folder" };
                var subDir = Path.Combine(vaultPath, "wiki", sub);
                if (Directory.Exists(subDir))
                {
                    foreach (var f in Directory.GetFiles(subDir, "*.md"))
                    {
                        var noteName = Path.GetFileNameWithoutExtension(f);
                        var relPath = Path.GetRelativePath(vaultPath, f).Replace('\\', '/');

                        // Filter by site relevance if known
                        bool isRelevant = true;
                        if (sub == "sources")
                        {
                            isRelevant = noteName.StartsWith(siteName, StringComparison.OrdinalIgnoreCase) ||
                                         noteName.Contains(siteName, StringComparison.OrdinalIgnoreCase);
                        }

                        if (isRelevant)
                        {
                            subNode.Children.Add(new SiteFileTreeNode
                            {
                                Name = Path.GetFileName(f),
                                Path = relPath,
                                Type = "file",
                                NoteType = sub
                            });
                        }
                    }
                }
                wikiNode.Children.Add(subNode);
            }
            root.Children.Add(wikiNode);

            return root;
        }
    }
}
