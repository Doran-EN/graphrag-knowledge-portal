using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ObsidianGraphRag.Shared.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ObsidianGraphRag.Shared.Services.Vault
{
    public class VaultGraphEngine
    {
        public string RootPath { get; }
        public bool IsConnected => Directory.Exists(RootPath);

        public Dictionary<string, ObsidianNote> Notes { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _nameToKeyMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aliasToKeyMap = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, HashSet<string>> _outgoingEdges = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _incomingEdges = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex WikiLinkRegex = new(@"\[\[([^\]\|]+)(?:\|[^\]]+)?\]\]", RegexOptions.Compiled);
        private readonly IDeserializer _yamlDeserializer;

        public VaultGraphEngine(string? rootPath = null)
        {
            RootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath)
                ? Path.Combine(AppContext.BaseDirectory, "knowledge")
                : rootPath);

            _yamlDeserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            Reload();
        }

        public VaultGraphEngine ReadSnapshot() => new(RootPath);

        public void Reload()
        {
            Notes.Clear();
            _nameToKeyMap.Clear();
            _aliasToKeyMap.Clear();
            _outgoingEdges.Clear();
            _incomingEdges.Clear();

            if (!Directory.Exists(RootPath))
                return;

            // 1. First Pass: Read all files in raw/ and wiki/
            var allFiles = Directory.GetFiles(RootPath, "*.md", SearchOption.AllDirectories);
            foreach (var file in allFiles)
            {
                var rel = Path.GetRelativePath(RootPath, file).Replace('\\', '/');
                if (rel.StartsWith(".") || rel.Contains("/.") || rel.StartsWith("wiki_backup_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var note = ParseNote(file);
                    var key = note.RelativePath.ToLower();

                    Notes[key] = note;
                    _nameToKeyMap[note.NoteName.ToLower()] = key;

                    // Register aliases for Entity Resolution
                    foreach (var alias in note.Aliases)
                    {
                        if (!string.IsNullOrWhiteSpace(alias))
                            _aliasToKeyMap[alias.Trim().ToLower()] = key;
                    }

                    _outgoingEdges[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _incomingEdges[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    // Ignore parse errors on corrupted scratch files
                }
            }

            // 2. Second Pass: Resolve WikiLinks and build Graph edges
            foreach (var (key, note) in Notes)
            {
                foreach (var link in note.OutgoingLinks)
                {
                    var resolvedKey = ResolveLink(note, link);
                    if (resolvedKey != null && Notes.ContainsKey(resolvedKey))
                    {
                        _outgoingEdges[key].Add(resolvedKey);
                        _incomingEdges[resolvedKey].Add(key);
                        if (!note.IncomingLinks.Contains(key))
                            Notes[resolvedKey].IncomingLinks.Add(key);
                    }
                }
            }
        }

        internal ObsidianNote ParseNote(string filePath)
        {
            var content = File.ReadAllText(filePath);
            var relPath = Path.GetRelativePath(RootPath, filePath).Replace('\\', '/');
            if (relPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                relPath = relPath[..^3];

            var noteName = Path.GetFileNameWithoutExtension(filePath);
            var note = new ObsidianNote
            {
                FilePath = filePath,
                RelativePath = relPath,
                NoteName = noteName,
                Title = noteName,
                Content = content,
                BodyContent = content
            };

            // Detect note type from directory
            var lowerRel = relPath.ToLower();
            if (lowerRel.StartsWith("raw/")) note.NoteType = TheSchemaDefinitions.TypeRaw;
            else if (lowerRel.StartsWith("wiki/sources/")) note.NoteType = TheSchemaDefinitions.TypeSource;
            else if (lowerRel.StartsWith("wiki/entities/")) note.NoteType = TheSchemaDefinitions.TypeEntity;
            else if (lowerRel.StartsWith("wiki/concepts/")) note.NoteType = TheSchemaDefinitions.TypeConcept;
            else if (lowerRel.StartsWith("wiki/comparisons/")) note.NoteType = TheSchemaDefinitions.TypeComparison;
            else if (lowerRel.StartsWith("wiki/overview/")) note.NoteType = TheSchemaDefinitions.TypeOverview;

            // Parse Frontmatter
            if (content.StartsWith("---"))
            {
                int endFm = content.IndexOf("---", 3, StringComparison.Ordinal);
                if (endFm > 0)
                {
                    var yamlText = content.Substring(3, endFm - 3).Trim();
                    note.BodyContent = content[(endFm + 3)..].Trim();

                    try
                    {
                        var dict = _yamlDeserializer.Deserialize<Dictionary<string, object>>(yamlText);
                        if (dict != null)
                        {
                            note.Frontmatter = new Dictionary<string, object>(dict, StringComparer.OrdinalIgnoreCase);

                            if (dict.TryGetValue("title", out var t) && t != null) note.Title = t.ToString() ?? noteName;
                            if (dict.TryGetValue("summary", out var s) && s != null) note.Summary = s.ToString() ?? "";
                            if (dict.TryGetValue("hash", out var h) && h != null) note.Hash = h.ToString() ?? "";
                            if (dict.TryGetValue("status", out var st) && st != null) note.Status = st.ToString() ?? "synced";

                            if (dict.TryGetValue("aliases", out var alObj) && alObj is List<object> alList)
                            {
                                note.Aliases = alList.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
                            }

                            if (dict.TryGetValue("sources", out var scObj) && scObj is List<object> scList)
                            {
                                note.Sources = scList.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
                            }

                            if (dict.TryGetValue("url", out var urlObj) && urlObj != null)
                            {
                                var urlStr = urlObj.ToString();
                                if (!string.IsNullOrEmpty(urlStr) && !note.Sources.Contains(urlStr))
                                    note.Sources.Add(urlStr);
                            }
                            if (dict.TryGetValue("source_url", out var suObj) && suObj != null)
                            {
                                var suStr = suObj.ToString();
                                if (!string.IsNullOrEmpty(suStr) && !note.Sources.Contains(suStr))
                                    note.Sources.Add(suStr);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore malformed frontmatter
                    }
                }
            }

            // Extract H1 Title if empty
            var h1Match = Regex.Match(note.BodyContent, @"^#\s+(.+)$", RegexOptions.Multiline);
            if (h1Match.Success && string.IsNullOrEmpty(note.Title))
            {
                note.Title = h1Match.Groups[1].Value.Trim();
            }

            // Extract WikiLinks
            var matches = WikiLinkRegex.Matches(content);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in matches)
            {
                var target = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(target) && seen.Add(target))
                {
                    note.OutgoingLinks.Add(target);
                }
            }

            return note;
        }

        public string? ResolveLink(ObsidianNote sourceNote, string link)
        {
            var clean = link.Replace('\\', '/').Trim();
            if (clean.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                clean = clean[..^3];

            // 1. Direct path or wiki/ prefix
            var testKeys = new[] { clean.ToLower(), $"wiki/{clean}".ToLower(), $"raw/{clean}".ToLower() };
            foreach (var tk in testKeys)
            {
                if (Notes.ContainsKey(tk)) return tk;
            }

            // 2. Exact note name match
            var noteName = Path.GetFileName(clean).ToLower();
            if (_nameToKeyMap.TryGetValue(noteName, out var targetKey))
                return targetKey;

            // 3. Alias match (Entity Resolution)
            if (_aliasToKeyMap.TryGetValue(noteName, out var aliasTargetKey))
                return aliasTargetKey;

            return null;
        }

        public List<ObsidianNote> Get1HopNeighbors(string noteKey)
        {
            var keyLower = noteKey.ToLower();
            var neighbors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_outgoingEdges.TryGetValue(keyLower, out var outSet))
                foreach (var o in outSet) neighbors.Add(o);

            if (_incomingEdges.TryGetValue(keyLower, out var inSet))
                foreach (var i in inSet) neighbors.Add(i);

            return neighbors.Where(Notes.ContainsKey).Select(k => Notes[k]).ToList();
        }

        public List<ObsidianNote> Get2HopNeighbors(string noteKey)
        {
            var hop1 = Get1HopNeighbors(noteKey);
            var set = new HashSet<string>(hop1.Select(n => n.RelativePath.ToLower()), StringComparer.OrdinalIgnoreCase);
            set.Add(noteKey.ToLower());

            foreach (var n in hop1)
            {
                var hop2 = Get1HopNeighbors(n.RelativePath);
                foreach (var h in hop2) set.Add(h.RelativePath.ToLower());
            }

            return set.Where(Notes.ContainsKey).Select(k => Notes[k]).ToList();
        }

        private static readonly string[] GroupPalette = new[]
        {
            "#38bdf8", // Sky Blue (e.g. asterspring)
            "#a855f7", // Vibrant Purple (e.g. site 2)
            "#10b981", // Emerald Green (e.g. site 3)
            "#f59e0b", // Amber (e.g. site 4)
            "#ec4899", // Rose Pink (e.g. site 5)
            "#06b6d4", // Cyan
            "#f97316", // Orange
            "#6366f1", // Indigo
            "#14b8a6", // Teal
            "#84cc16"  // Lime
        };

        public List<string> DetectAllVaultSites()
        {
            var sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rawDir = Path.Combine(RootPath, "raw");
            if (Directory.Exists(rawDir))
            {
                foreach (var dir in Directory.GetDirectories(rawDir))
                {
                    var name = Path.GetFileName(dir);
                    if (!name.StartsWith(".") && !name.StartsWith("_"))
                        sites.Add(name);
                }
            }

            var sourcesDir = Path.Combine(RootPath, "wiki", "sources");
            if (Directory.Exists(sourcesDir))
            {
                foreach (var dir in Directory.GetDirectories(sourcesDir))
                {
                    var name = Path.GetFileName(dir);
                    if (!name.StartsWith(".") && !name.StartsWith("_"))
                        sites.Add(name);
                }
            }

            return sites.ToList();
        }

        public string ResolveNodeGroup(ObsidianNote note, List<string> knownSites)
        {
            var pathLower = note.RelativePath.ToLower();

            // 1. Explicit frontmatter
            if (note.Frontmatter != null)
            {
                if (note.Frontmatter.TryGetValue("site", out var s) && s != null) return s.ToString()!;
                if (note.Frontmatter.TryGetValue("group", out var g) && g != null) return g.ToString()!;
            }

            // 2. Direct folder path match
            foreach (var site in knownSites)
            {
                var sLower = site.ToLower();
                if (pathLower.Contains($"raw/{sLower}/") || pathLower.Contains($"sources/{sLower}/") || pathLower.Contains($"/{sLower}/"))
                    return site;
            }

            // 3. Match from sources URLs
            foreach (var src in note.Sources)
            {
                var srcLower = src.ToLower();
                foreach (var site in knownSites)
                {
                    if (srcLower.Contains(site.ToLower()))
                        return site;
                }
            }

            // 4. Match from 1-hop neighbor
            var neighbors = Get1HopNeighbors(note.RelativePath);
            foreach (var nbr in neighbors)
            {
                var nbrPath = nbr.RelativePath.ToLower();
                foreach (var site in knownSites)
                {
                    var sLower = site.ToLower();
                    if (nbrPath.Contains($"raw/{sLower}/") || nbrPath.Contains($"sources/{sLower}/"))
                        return site;
                }
            }

            // 5. Default by note type
            if (note.NoteType == TheSchemaDefinitions.TypeConcept) return "concepts";
            if (note.NoteType == TheSchemaDefinitions.TypeEntity) return "entities";

            return knownSites.Count > 0 ? knownSites[0] : "general";
        }

        public GraphDataDto ExportGraphData(string siteFilter = "")
        {
            var dto = new GraphDataDto();
            var filterLower = (siteFilter ?? "").Trim().ToLower();
            if (filterLower == "all") filterLower = "";

            var knownSites = DetectAllVaultSites();
            var groupColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int colorIdx = 0;
            foreach (var s in knownSites)
            {
                groupColors[s] = GroupPalette[colorIdx % GroupPalette.Length];
                colorIdx++;
            }
            if (!groupColors.ContainsKey("concepts"))
            {
                groupColors["concepts"] = "#a855f7"; // purple
            }
            if (!groupColors.ContainsKey("entities"))
            {
                groupColors["entities"] = "#10b981"; // green
            }
            if (!groupColors.ContainsKey("general"))
            {
                groupColors["general"] = "#64748b"; // slate
            }

            var includedNotes = Notes.Values
                .Where(n => string.IsNullOrEmpty(filterLower) ||
                            n.RelativePath.ToLower().Contains(filterLower) ||
                            n.Sources.Any(s => s.ToLower().Contains(filterLower)) ||
                            Get1HopNeighbors(n.RelativePath).Any(nbr => nbr.RelativePath.ToLower().Contains(filterLower)))
                .ToList();

            var includedKeys = new HashSet<string>(includedNotes.Select(n => n.RelativePath.ToLower()), StringComparer.OrdinalIgnoreCase);
            var groupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var note in includedNotes)
            {
                int degree = (_outgoingEdges.GetValueOrDefault(note.RelativePath.ToLower())?.Count ?? 0) +
                             (_incomingEdges.GetValueOrDefault(note.RelativePath.ToLower())?.Count ?? 0);

                var group = ResolveNodeGroup(note, knownSites);
                if (!groupColors.TryGetValue(group, out var color))
                {
                    color = GroupPalette[colorIdx % GroupPalette.Length];
                    groupColors[group] = color;
                    colorIdx++;
                }

                groupCounts[group] = groupCounts.GetValueOrDefault(group) + 1;

                dto.Nodes.Add(new GraphNodeDto
                {
                    Id = note.RelativePath.ToLower(),
                    Label = string.IsNullOrEmpty(note.Title) ? note.NoteName : note.Title,
                    Type = note.NoteType,
                    Summary = note.Summary,
                    Degree = degree,
                    Group = group,
                    Color = color
                });
            }

            foreach (var (grp, cnt) in groupCounts)
            {
                dto.Groups.Add(new GraphGroupDto
                {
                    Name = grp,
                    Color = groupColors.GetValueOrDefault(grp, "#38bdf8"),
                    NodeCount = cnt
                });
            }

            int edgeId = 0;
            foreach (var (src, targets) in _outgoingEdges)
            {
                if (!includedKeys.Contains(src)) continue;
                foreach (var tgt in targets)
                {
                    if (!includedKeys.Contains(tgt)) continue;
                    dto.Edges.Add(new GraphEdgeDto
                    {
                        Id = $"e_{edgeId++}",
                        Source = src,
                        Target = tgt,
                        Label = "links_to"
                    });
                }
            }

            return dto;
        }
    }
}
