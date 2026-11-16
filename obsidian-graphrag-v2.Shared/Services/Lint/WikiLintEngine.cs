using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ObsidianGraphRag.Shared.Models;
using ObsidianGraphRag.Shared.Services.LLM;
using ObsidianGraphRag.Shared.Services.Vault;

namespace ObsidianGraphRag.Shared.Services.Lint
{
    public class WikiLintEngine
    {
        private readonly VaultGraphEngine _graphEngine;
        private readonly ILlmClient _llmClient;

        public LintReport CurrentReport { get; private set; } = new();

        public WikiLintEngine(VaultGraphEngine graphEngine, ILlmClient llmClient)
        {
            _graphEngine = graphEngine;
            _llmClient = llmClient;
        }

        public async Task<LintReport> RunLintAsync(bool includeLlmDeepScan = false)
        {
            _graphEngine.Reload();
            var report = new LintReport
            {
                TotalNotesChecked = _graphEngine.Notes.Count
            };

            var notes = _graphEngine.Notes.Values.ToList();

            // 1. Orphan Nodes (0 incoming & 0 outgoing links)
            foreach (var note in notes)
            {
                if (note.NoteType == TheSchemaDefinitions.TypeOverview) continue;
                if (note.OutgoingLinks.Count == 0 && note.IncomingLinks.Count == 0)
                {
                    report.OrphanPagesCount++;
                    report.Issues.Add(new LintIssue
                    {
                        Type = LintIssueType.OrphanPage,
                        Title = $"孤岛笔记 (Orphan Note): {note.NoteName}",
                        Description = $"笔记未被任何其他页面引用，也没有向外引用其他节点。",
                        TargetNotePath = note.FilePath,
                        CanAutoFix = false
                    });
                }
            }

            // 2. Broken Links (links that do not resolve to any existing note)
            foreach (var note in notes)
            {
                foreach (var link in note.OutgoingLinks)
                {
                    var resolved = _graphEngine.ResolveLink(note, link);
                    if (resolved == null)
                    {
                        report.BrokenLinksCount++;
                        report.Issues.Add(new LintIssue
                        {
                            Type = LintIssueType.BrokenLink,
                            Title = $"断链/死链 (Broken Link): [[{link}]]",
                            Description = $"在笔记 [{note.NoteName}] 中引用了不存在的文件 [[{link}]]",
                            TargetNotePath = note.FilePath,
                            Snippet = link,
                            CanAutoFix = true,
                            AutoFixActionName = "Remove Broken Link"
                        });
                    }
                }
            }

            // 3. Unlinked Mentions (Text contains known entity names/aliases without [[...]])
            var entityNames = notes
                .Where(n => n.NoteType == TheSchemaDefinitions.TypeEntity || n.NoteType == TheSchemaDefinitions.TypeConcept)
                .Select(n => new { Name = n.NoteName, Aliases = n.Aliases, TargetPath = n.RelativePath })
                .ToList();

            foreach (var note in notes)
            {
                if (note.NoteType == TheSchemaDefinitions.TypeOverview) continue;
                var text = note.BodyContent;

                foreach (var target in entityNames)
                {
                    if (target.TargetPath.Equals(note.RelativePath, StringComparison.OrdinalIgnoreCase)) continue;

                    var namesToLook = new List<string> { target.Name };
                    namesToLook.AddRange(target.Aliases);

                    foreach (var name in namesToLook)
                    {
                        if (name.Length < 3) continue;
                        if (text.Contains(name) && !text.Contains($"[[{target.TargetPath}]]") && !text.Contains($"[[{target.Name}]]"))
                        {
                            report.UnlinkedMentionsCount++;
                            report.Issues.Add(new LintIssue
                            {
                                Type = LintIssueType.UnlinkedMention,
                                Title = $"未打链接词条: \"{name}\"",
                                Description = $"笔记 [{note.NoteName}] 正文中提到了实体 \"{name}\"，但未加双向链接。",
                                TargetNotePath = note.FilePath,
                                RelatedEntityOrConcept = target.TargetPath,
                                Snippet = name,
                                CanAutoFix = true,
                                AutoFixActionName = $"Link to [[{target.TargetPath}]]",
                                AutoFixPayload = name
                            });
                            break;
                        }
                    }
                }
            }

            // 4. Suspicious Alias Explosion (Any node with > 8 aliases is flagged as a potential black hole)
            foreach (var note in notes)
            {
                if (note.NoteType == TheSchemaDefinitions.TypeOverview) continue;
                if (note.Aliases.Count > 8)
                {
                    report.Issues.Add(new LintIssue
                    {
                        Type = LintIssueType.SuspiciousAliasExplosion,
                        Title = $"疑似别名黑洞膨胀 (Alias Explosion): {note.NoteName} ({note.Aliases.Count} 个别名)",
                        Description = $"节点 [{note.NoteName}] 包含了 {note.Aliases.Count} 个别名，显著超过健康阈值 (8)。可能存在子概念、独立规则或不同类型实体被误当同义词吞并的情况。",
                        TargetNotePath = note.FilePath,
                        CanAutoFix = false
                    });
                }
            }

            var aliasExplosionCount = report.Issues.Count(i => i.Type == LintIssueType.SuspiciousAliasExplosion);
            int penalty = (report.OrphanPagesCount * 5) + (report.BrokenLinksCount * 8) + (report.UnlinkedMentionsCount * 2) + (aliasExplosionCount * 15);
            report.HealthScore = Math.Max(10, 100 - penalty);

            CurrentReport = report;
            return report;
        }

        public async Task<bool> ApplyAutoFixAsync(LintIssue issue)
        {
            if (!File.Exists(issue.TargetNotePath)) return false;

            var content = await File.ReadAllTextAsync(issue.TargetNotePath);

            if (issue.Type == LintIssueType.UnlinkedMention && !string.IsNullOrEmpty(issue.AutoFixPayload))
            {
                var word = issue.AutoFixPayload;
                var replacement = $"[[{issue.RelatedEntityOrConcept}|{word}]]";
                var regex = new Regex(Regex.Escape(word), RegexOptions.None);
                content = regex.Replace(content, replacement, 3);
                await File.WriteAllTextAsync(issue.TargetNotePath, content);
                issue.IsFixed = true;

                await AppendLogAsync($"auto-link | Linked \"{word}\" in {Path.GetFileName(issue.TargetNotePath)} -> [[{issue.RelatedEntityOrConcept}]]");
                return true;
            }

            if (issue.Type == LintIssueType.BrokenLink && !string.IsNullOrEmpty(issue.Snippet))
            {
                var broken = @"\[\[" + Regex.Escape(issue.Snippet) + @"(?:\|[^\]]+)?\]\]";
                content = Regex.Replace(content, broken, issue.Snippet);
                await File.WriteAllTextAsync(issue.TargetNotePath, content);
                issue.IsFixed = true;

                await AppendLogAsync($"broken-link-fix | Cleaned dead link [[{issue.Snippet}]] in {Path.GetFileName(issue.TargetNotePath)}");
                return true;
            }

            return false;
        }

        private async Task AppendLogAsync(string entry)
        {
            var wikiPath = Path.Combine(_graphEngine.RootPath, "wiki");
            Directory.CreateDirectory(wikiPath);
            var logPath = Path.Combine(wikiPath, "log.md");
            await File.AppendAllTextAsync(logPath, $"## [{DateTime.Now:yyyy-MM-dd}] lint | {entry}\n");
        }
    }
}
