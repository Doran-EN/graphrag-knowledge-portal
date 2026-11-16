using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ObsidianGraphRag.Shared.Models;

namespace ObsidianGraphRag.Shared.Services.Ingestion.Pipeline
{
    public class ResolutionResult
    {
        public bool IsNew { get; set; }
        public string CanonicalName { get; set; } = string.Empty;
        public string WikiLink { get; set; } = string.Empty;
        public string ActionTaken { get; set; } = "REUSED"; // REUSED, ALIASED, SUBCONCEPT_CREATED, ENTITY_CREATED, CONCEPT_CREATED, DOCUMENT_CREATED
    }

    public static class EntityResolutionService
    {
        /// <summary>
        /// Main 4-Way Decision Knowledge Node Resolver
        /// Strictly dispatches based on relation_type with type-compatibility guards.
        /// </summary>
        public static async Task<ResolutionResult> ResolveKnowledgeCandidateAsync(
            string vaultPath,
            KnowledgeNodeCandidate candidate,
            string currentRawRelPath,
            string currentSourceGraphLink,
            KnowledgeCatalog catalog)
        {
            // 1. Validation check
            var val = EntityValidator.Validate(candidate.Name);
            if (!val.IsValid)
            {
                return new ResolutionResult { IsNew = false, CanonicalName = string.Empty, WikiLink = string.Empty, ActionTaken = "REJECTED" };
            }

            var cleanName = val.CleanedName;
            candidate.Name = cleanName;

            // 2. Pre-check: In-memory Catalog Match (Exact canonical title or legitimate alias match)
            var existing = catalog.FindMatch(cleanName);
            if (existing != null)
            {
                bool isExactTitle = existing.Title.Equals(cleanName, StringComparison.OrdinalIgnoreCase);
                bool isSubconceptRelation = string.Equals(candidate.RelationType, KnowledgeRelationTypes.SubconceptOf, StringComparison.OrdinalIgnoreCase);
                bool isTargetOfCandidate = (existing.Title.Equals(candidate.TargetCanonicalName, StringComparison.OrdinalIgnoreCase)
                    || candidate.ParentConcepts.Contains(existing.Title, StringComparer.OrdinalIgnoreCase));

                // If candidate is a subconcept, but matched an existing node by ALIAS of the parent,
                // do NOT short-circuit as REUSED parent! Let it proceed to SUBCONCEPT_OF creation.
                if (!isSubconceptRelation || (!isTargetOfCandidate && isExactTitle))
                {
                    if (!existing.Sources.Contains(currentRawRelPath, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.Sources.Add(currentRawRelPath);
                        await AppendSourceAndLinkToMarkdownAsync(existing.FilePath, currentRawRelPath, currentSourceGraphLink);
                    }

                    if (!isExactTitle && IsTypeCompatible(candidate, existing))
                    {
                        await AppendAliasToMarkdownAsync(existing.FilePath, cleanName);
                        catalog.AddAliasToEntry(existing.Title, cleanName);
                    }

                    var folder = existing.Kind == "entity" ? "entities" : "concepts";
                    return new ResolutionResult
                    {
                        IsNew = false,
                        CanonicalName = existing.Title,
                        WikiLink = $"[[{folder}/{existing.Title}]]",
                        ActionTaken = "REUSED"
                    };
                }
            }

            // 3. Strict Relation Type Dispatch
            var relType = candidate.RelationType?.Trim().ToUpperInvariant() ?? KnowledgeRelationTypes.Distinct;

            // Check if candidate is a Document entity by type or naming convention
            bool isDocumentEntity = string.Equals(relType, KnowledgeRelationTypes.Document, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(candidate.Kind, "entity", StringComparison.OrdinalIgnoreCase) && string.Equals(candidate.EntityType, "Document", StringComparison.OrdinalIgnoreCase))
                || cleanName.Contains("Guide", StringComparison.OrdinalIgnoreCase)
                || cleanName.Contains("Guideline", StringComparison.OrdinalIgnoreCase)
                || cleanName.Contains("Standard v", StringComparison.OrdinalIgnoreCase)
                || cleanName.Contains("Act 19", StringComparison.OrdinalIgnoreCase)
                || cleanName.Contains("Act 20", StringComparison.OrdinalIgnoreCase);

            if (isDocumentEntity && relType != KnowledgeRelationTypes.SameAs)
            {
                relType = KnowledgeRelationTypes.Document;
            }

            switch (relType)
            {
                case KnowledgeRelationTypes.SameAs:
                    return await ResolveSameAsAsync(vaultPath, candidate, currentRawRelPath, currentSourceGraphLink, catalog);

                case KnowledgeRelationTypes.SubconceptOf:
                    return await ResolveSubconceptAsync(vaultPath, candidate, currentRawRelPath, currentSourceGraphLink, catalog);

                case KnowledgeRelationTypes.Document:
                    return await ResolveDocumentAsync(vaultPath, candidate, currentRawRelPath, currentSourceGraphLink, catalog);

                case KnowledgeRelationTypes.RelatedTo:
                case KnowledgeRelationTypes.Distinct:
                default:
                    return await ResolveDistinctAsync(vaultPath, candidate, currentRawRelPath, currentSourceGraphLink, catalog);
            }
        }

        // --- 3.1. SAME_AS: Strict Synonym / Translation / Abbreviation ---
        private static async Task<ResolutionResult> ResolveSameAsAsync(
            string vaultPath,
            KnowledgeNodeCandidate candidate,
            string currentRawRelPath,
            string currentSourceGraphLink,
            KnowledgeCatalog catalog)
        {
            var cleanName = candidate.Name;
            var targetName = !string.IsNullOrWhiteSpace(candidate.TargetCanonicalName) ? candidate.TargetCanonicalName : cleanName;
            var targetEntry = catalog.FindMatch(targetName);

            if (targetEntry == null)
            {
                // Target does not exist yet. Fall back to distinct creation so information is not lost.
                return await ResolveDistinctAsync(vaultPath, candidate, currentRawRelPath, currentSourceGraphLink, catalog);
            }

            // TYPE COMPATIBILITY GUARD:
            // Prevents Person -> Organization, Product -> Organization, Document -> Concept aliasing disasters!
            if (!IsTypeCompatible(candidate, targetEntry))
            {
                return await ResolveIncompatibleFallbackAsync(vaultPath, candidate, targetEntry, currentRawRelPath, currentSourceGraphLink, catalog);
            }

            // Compatible SAME_AS: Append as alias into existing canonical node
            await AppendAliasToMarkdownAsync(targetEntry.FilePath, cleanName);
            catalog.AddAliasToEntry(targetEntry.Title, cleanName);

            if (!targetEntry.Sources.Contains(currentRawRelPath, StringComparer.OrdinalIgnoreCase))
            {
                targetEntry.Sources.Add(currentRawRelPath);
                await AppendSourceAndLinkToMarkdownAsync(targetEntry.FilePath, currentRawRelPath, currentSourceGraphLink);
            }

            var f = targetEntry.Kind == "entity" ? "entities" : "concepts";
            return new ResolutionResult
            {
                IsNew = false,
                CanonicalName = targetEntry.Title,
                WikiLink = $"[[{f}/{targetEntry.Title}]]",
                ActionTaken = "ALIASED"
            };
        }

        // --- Incompatible Fallback: Guard against cross-type alias pollution ---
        private static async Task<ResolutionResult> ResolveIncompatibleFallbackAsync(
            string vaultPath,
            KnowledgeNodeCandidate candidate,
            KnowledgeCatalogEntry targetEntry,
            string currentRawRelPath,
            string currentSourceGraphLink,
            KnowledgeCatalog catalog)
        {
            var cleanName = candidate.Name;
            var targetFolder = targetEntry.Kind == "entity" ? "entities" : "concepts";

            if (string.Equals(candidate.Kind, "concept", StringComparison.OrdinalIgnoreCase))
            {
                if (!candidate.ParentConcepts.Contains(targetEntry.Title, StringComparer.OrdinalIgnoreCase))
                {
                    candidate.ParentConcepts.Add(targetEntry.Title);
                }
                return await ResolveSubconceptAsync(vaultPath, candidate, currentRawRelPath, currentSourceGraphLink, catalog);
            }
            else
            {
                var entDir = Path.Combine(vaultPath, "wiki", "entities");
                Directory.CreateDirectory(entDir);
                var filePath = Path.Combine(entDir, $"{cleanName}.md");

                var sourcesList = new List<string> { currentRawRelPath };
                var graphLinks = new List<string> { currentSourceGraphLink };

                var candType = NormalizeEntityType(candidate.EntityType);
                if (candType == "Person")
                    graphLinks.Add($"关联机构: [[{targetFolder}/{targetEntry.Title}]]");
                else if (candType == "Product")
                    graphLinks.Add($"出品/所属机构: [[{targetFolder}/{targetEntry.Title}]]");
                else
                    graphLinks.Add($"关联节点: [[{targetFolder}/{targetEntry.Title}]]");

                var entityMarkdown = TheSchemaDefinitions.GenerateEntityTemplate(
                    cleanName,
                    candType,
                    string.IsNullOrWhiteSpace(candidate.Summary) ? $"{cleanName} ({candType}) 独立知识实体。" : candidate.Summary,
                    candidate.Aliases,
                    sourcesList,
                    string.IsNullOrWhiteSpace(candidate.Details) ? $"与 [[{targetFolder}/{targetEntry.Title}]] 关联。" : candidate.Details,
                    graphLinks
                );

                await File.WriteAllTextAsync(filePath, entityMarkdown);

                catalog.Register(new KnowledgeCatalogEntry
                {
                    Title = cleanName,
                    Kind = "entity",
                    EntityType = candType,
                    Aliases = candidate.Aliases,
                    Sources = sourcesList,
                    Summary = candidate.Summary,
                    FilePath = filePath
                });

                return new ResolutionResult
                {
                    IsNew = true,
                    CanonicalName = cleanName,
                    WikiLink = $"[[entities/{cleanName}]]",
                    ActionTaken = "ENTITY_CREATED"
                };
            }
        }

        // --- 3.2. SUBCONCEPT_OF: Independent Rule / Sub-concept / Workflow ---
        private static async Task<ResolutionResult> ResolveSubconceptAsync(
            string vaultPath,
            KnowledgeNodeCandidate candidate,
            string currentRawRelPath,
            string currentSourceGraphLink,
            KnowledgeCatalog catalog)
        {
            var cleanName = candidate.Name;
            var parentName = candidate.ParentConcepts.FirstOrDefault() ?? candidate.TargetCanonicalName ?? string.Empty;
            var conceptDir = Path.Combine(vaultPath, "wiki", "concepts");
            Directory.CreateDirectory(conceptDir);
            var filePath = Path.Combine(conceptDir, $"{cleanName}.md");

            var sourcesList = new List<string> { currentRawRelPath };
            var graphLinks = new List<string> { currentSourceGraphLink };
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                graphLinks.Add($"[[concepts/{parentName}]]");
            }

            var detailsText = string.IsNullOrWhiteSpace(candidate.Details) ? "属于特定细分业务规则或流程。" : candidate.Details;
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                detailsText += $"\n\n所属父级概念: [[concepts/{parentName}]]";
            }

            var conceptMarkdown = TheSchemaDefinitions.GenerateConceptTemplate(
                cleanName,
                string.IsNullOrWhiteSpace(candidate.Summary) ? $"{cleanName} 细分概念规则。" : candidate.Summary,
                candidate.Aliases,
                sourcesList,
                detailsText,
                graphLinks
            );

            await File.WriteAllTextAsync(filePath, conceptMarkdown);

            // If parent concept note exists, append this subconcept to it!
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                var parentEntry = catalog.FindMatch(parentName);
                if (parentEntry != null && File.Exists(parentEntry.FilePath))
                {
                    await AppendSubconceptToParentMarkdownAsync(parentEntry.FilePath, $"[[concepts/{cleanName}]]");
                }
            }

            catalog.Register(new KnowledgeCatalogEntry
            {
                Title = cleanName,
                Kind = "concept",
                Aliases = candidate.Aliases,
                Sources = sourcesList,
                Summary = candidate.Summary,
                FilePath = filePath
            });

            return new ResolutionResult
            {
                IsNew = true,
                CanonicalName = cleanName,
                WikiLink = $"[[concepts/{cleanName}]]",
                ActionTaken = "SUBCONCEPT_CREATED"
            };
        }

        // --- 3.3. Document Entity (e.g. e-Invoice Guide v2.1) ---
        private static async Task<ResolutionResult> ResolveDocumentAsync(
            string vaultPath,
            KnowledgeNodeCandidate candidate,
            string currentRawRelPath,
            string currentSourceGraphLink,
            KnowledgeCatalog catalog)
        {
            var cleanName = candidate.Name;
            var entityDir = Path.Combine(vaultPath, "wiki", "entities");
            Directory.CreateDirectory(entityDir);
            var filePath = Path.Combine(entityDir, $"{cleanName}.md");

            var sourcesList = new List<string> { currentRawRelPath };
            var graphLinks = new List<string> { currentSourceGraphLink };

            if (!string.IsNullOrWhiteSpace(candidate.PublishedBy))
                graphLinks.Add($"发布机构: [[entities/{candidate.PublishedBy}]]");
            if (!string.IsNullOrWhiteSpace(candidate.TargetCanonicalName))
                graphLinks.Add($"规范主题: [[concepts/{candidate.TargetCanonicalName}]]");

            var entityMarkdown = TheSchemaDefinitions.GenerateEntityTemplate(
                cleanName,
                "Document",
                string.IsNullOrWhiteSpace(candidate.Summary) ? $"官方标准与操作指南文档。" : candidate.Summary,
                candidate.Aliases,
                sourcesList,
                string.IsNullOrWhiteSpace(candidate.Details) ? "提供合规要求、数据格式及操作指引规范。" : candidate.Details,
                graphLinks
            );

            await File.WriteAllTextAsync(filePath, entityMarkdown);

            catalog.Register(new KnowledgeCatalogEntry
            {
                Title = cleanName,
                Kind = "entity",
                EntityType = "Document",
                Aliases = candidate.Aliases,
                Sources = sourcesList,
                Summary = candidate.Summary,
                FilePath = filePath
            });

            return new ResolutionResult
            {
                IsNew = true,
                CanonicalName = cleanName,
                WikiLink = $"[[entities/{cleanName}]]",
                ActionTaken = "DOCUMENT_CREATED"
            };
        }

        // --- 3.4. Distinct Entity or Concept ---
        private static async Task<ResolutionResult> ResolveDistinctAsync(
            string vaultPath,
            KnowledgeNodeCandidate candidate,
            string currentRawRelPath,
            string currentSourceGraphLink,
            KnowledgeCatalog catalog)
        {
            var cleanName = candidate.Name;

            if (string.Equals(candidate.Kind, "concept", StringComparison.OrdinalIgnoreCase))
            {
                var conceptDir = Path.Combine(vaultPath, "wiki", "concepts");
                Directory.CreateDirectory(conceptDir);
                var filePath = Path.Combine(conceptDir, $"{cleanName}.md");

                var sourcesList = new List<string> { currentRawRelPath };
                var graphLinks = new List<string> { currentSourceGraphLink };
                foreach (var rel in candidate.RelatedEntities)
                {
                    graphLinks.Add($"[[entities/{rel}]]");
                }

                var conceptMarkdown = TheSchemaDefinitions.GenerateConceptTemplate(
                    cleanName,
                    string.IsNullOrWhiteSpace(candidate.Summary) ? $"{cleanName} 核心业务概念。" : candidate.Summary,
                    candidate.Aliases,
                    sourcesList,
                    string.IsNullOrWhiteSpace(candidate.Details) ? "核心业务概念与规则定义。" : candidate.Details,
                    graphLinks
                );

                await File.WriteAllTextAsync(filePath, conceptMarkdown);

                catalog.Register(new KnowledgeCatalogEntry
                {
                    Title = cleanName,
                    Kind = "concept",
                    Aliases = candidate.Aliases,
                    Sources = sourcesList,
                    Summary = candidate.Summary,
                    FilePath = filePath
                });

                return new ResolutionResult
                {
                    IsNew = true,
                    CanonicalName = cleanName,
                    WikiLink = $"[[concepts/{cleanName}]]",
                    ActionTaken = "CONCEPT_CREATED"
                };
            }
            else
            {
                var entDir = Path.Combine(vaultPath, "wiki", "entities");
                Directory.CreateDirectory(entDir);
                var entFilePath = Path.Combine(entDir, $"{cleanName}.md");

                var entSources = new List<string> { currentRawRelPath };
                var entGraphLinks = new List<string> { currentSourceGraphLink };
                foreach (var pc in candidate.ParentConcepts)
                {
                    entGraphLinks.Add($"[[concepts/{pc}]]");
                }

                var entType = NormalizeEntityType(candidate.EntityType);

                var entMarkdown = TheSchemaDefinitions.GenerateEntityTemplate(
                    cleanName,
                    entType,
                    string.IsNullOrWhiteSpace(candidate.Summary) ? $"{cleanName} ({entType}) 业务实体。" : candidate.Summary,
                    candidate.Aliases,
                    entSources,
                    string.IsNullOrWhiteSpace(candidate.Details) ? $"{cleanName} 详细属性与信息。" : candidate.Details,
                    entGraphLinks
                );

                await File.WriteAllTextAsync(entFilePath, entMarkdown);

                catalog.Register(new KnowledgeCatalogEntry
                {
                    Title = cleanName,
                    Kind = "entity",
                    EntityType = entType,
                    Aliases = candidate.Aliases,
                    Sources = entSources,
                    Summary = candidate.Summary,
                    FilePath = entFilePath
                });

                return new ResolutionResult
                {
                    IsNew = true,
                    CanonicalName = cleanName,
                    WikiLink = $"[[entities/{cleanName}]]",
                    ActionTaken = "ENTITY_CREATED"
                };
            }
        }

        // --- Type Compatibility Guard Helper Methods ---
        private static bool IsTypeCompatible(KnowledgeNodeCandidate candidate, KnowledgeCatalogEntry targetEntry)
        {
            var candKind = candidate.Kind?.Trim().ToLowerInvariant() ?? "entity";
            var targetKind = targetEntry.Kind?.Trim().ToLowerInvariant() ?? "entity";

            // Concept vs Entity cannot be merged as SAME_AS
            if (candKind != targetKind)
            {
                return false;
            }

            if (candKind == "concept")
            {
                return true;
            }

            var candType = NormalizeEntityType(candidate.EntityType);
            var targetType = NormalizeEntityType(targetEntry.EntityType);

            // Person can ONLY merge with Person
            if (candType == "Person" || targetType == "Person")
            {
                return candType == targetType;
            }

            // Product can ONLY merge with Product (never with Organization)
            if (candType == "Product" || targetType == "Product")
            {
                return candType == targetType;
            }

            // Document can ONLY merge with Document (never with Concept or Org)
            if (candType == "Document" || targetType == "Document")
            {
                return candType == targetType;
            }

            // Organization can merge with Organization or General
            if (candType == "Organization" && targetType == "Organization")
            {
                return true;
            }

            return candType == targetType || candType == "General" || targetType == "General";
        }

        private static string NormalizeEntityType(string? type)
        {
            if (string.IsNullOrWhiteSpace(type)) return "General";
            var t = type.Trim();
            if (t.Equals("Organization", StringComparison.OrdinalIgnoreCase) || t.Equals("Company", StringComparison.OrdinalIgnoreCase)) return "Organization";
            if (t.Equals("Person", StringComparison.OrdinalIgnoreCase) || t.Equals("People", StringComparison.OrdinalIgnoreCase) || t.Equals("Individual", StringComparison.OrdinalIgnoreCase)) return "Person";
            if (t.Equals("Product", StringComparison.OrdinalIgnoreCase) || t.Equals("Software", StringComparison.OrdinalIgnoreCase) || t.Equals("Hardware", StringComparison.OrdinalIgnoreCase) || t.Equals("Terminal", StringComparison.OrdinalIgnoreCase)) return "Product";
            if (t.Equals("Document", StringComparison.OrdinalIgnoreCase) || t.Equals("Guide", StringComparison.OrdinalIgnoreCase) || t.Equals("Standard", StringComparison.OrdinalIgnoreCase) || t.Equals("Act", StringComparison.OrdinalIgnoreCase)) return "Document";
            if (t.Equals("Service", StringComparison.OrdinalIgnoreCase)) return "Service";
            return t;
        }

        // Compatibility method for legacy calls
        public static async Task<ResolutionResult> ResolveAndPersistEntityAsync(
            string vaultPath,
            EntityCandidate candidate,
            string currentRawRelPath,
            string currentSourceGraphLink,
            KnowledgeCatalog catalog)
        {
            return await ResolveKnowledgeCandidateAsync(
                vaultPath,
                new KnowledgeNodeCandidate
                {
                    Name = candidate.Name,
                    Kind = "entity",
                    EntityType = candidate.Type,
                    Aliases = candidate.Aliases,
                    Summary = candidate.Summary,
                    Details = candidate.Relations,
                    RelationType = KnowledgeRelationTypes.Distinct
                },
                currentRawRelPath,
                currentSourceGraphLink,
                catalog
            );
        }

        // Compatibility method for legacy calls
        public static async Task<ResolutionResult> ResolveAndPersistConceptAsync(
            string vaultPath,
            ConceptCandidate candidate,
            string currentRawRelPath,
            string currentSourceGraphLink,
            KnowledgeCatalog catalog)
        {
            return await ResolveKnowledgeCandidateAsync(
                vaultPath,
                new KnowledgeNodeCandidate
                {
                    Name = candidate.Name,
                    Kind = "concept",
                    Aliases = candidate.Aliases,
                    Summary = candidate.Summary,
                    Details = candidate.Details,
                    RelationType = KnowledgeRelationTypes.Distinct
                },
                currentRawRelPath,
                currentSourceGraphLink,
                catalog
            );
        }

        public static async Task AppendAliasToMarkdownAsync(string filePath, string newAlias)
        {
            try
            {
                if (!File.Exists(filePath) || string.IsNullOrWhiteSpace(newAlias)) return;
                var text = await File.ReadAllTextAsync(filePath);

                var cleanAlias = newAlias.Trim().Replace("\"", "\\\"");

                if (text.Contains($"\"{cleanAlias}\"")) return; // already in aliases

                if (text.Contains("aliases: ["))
                {
                    var idx = text.IndexOf("aliases: [");
                    var closeIdx = text.IndexOf("]", idx);
                    if (closeIdx > idx)
                    {
                        var inside = text.Substring(idx + 10, closeIdx - (idx + 10)).Trim();
                        var newInside = string.IsNullOrEmpty(inside) ? $"\"{cleanAlias}\"" : $"{inside}, \"{cleanAlias}\"";
                        text = text[..(idx + 10)] + newInside + text[closeIdx..];
                        await File.WriteAllTextAsync(filePath, text);
                    }
                }
            }
            catch { }
        }

        public static async Task AppendSubconceptToParentMarkdownAsync(string parentFilePath, string subconceptWikiLink)
        {
            try
            {
                if (!File.Exists(parentFilePath) || string.IsNullOrWhiteSpace(subconceptWikiLink)) return;
                var text = await File.ReadAllTextAsync(parentFilePath);

                if (text.Contains(subconceptWikiLink)) return;

                if (text.Contains("## 细分与衍生子概念"))
                {
                    var idx = text.IndexOf("## 细分与衍生子概念");
                    var nextHeader = text.IndexOf("\n## ", idx + 14);
                    var insertPos = nextHeader > idx ? nextHeader : text.Length;
                    text = text.Insert(insertPos, $"\n- {subconceptWikiLink}");
                }
                else
                {
                    text += $"\n\n## 细分与衍生子概念 (Subconcepts)\n- {subconceptWikiLink}\n";
                }

                await File.WriteAllTextAsync(parentFilePath, text);
            }
            catch { }
        }

        public static async Task AppendSourceAndLinkToMarkdownAsync(string filePath, string newRawSource, string newGraphLink)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                var text = await File.ReadAllTextAsync(filePath);

                bool modified = false;

                // 1. Update frontmatter sources: [...]
                if (!string.IsNullOrWhiteSpace(newRawSource) && !text.Contains(newRawSource) && text.Contains("sources: ["))
                {
                    var idx = text.IndexOf("sources: [");
                    var closeIdx = text.IndexOf("]", idx);
                    if (closeIdx > idx)
                    {
                        var inside = text.Substring(idx + 10, closeIdx - (idx + 10)).Trim();
                        var newInside = string.IsNullOrEmpty(inside) ? $"\"{newRawSource}\"" : $"{inside}, \"{newRawSource}\"";
                        text = text[..(idx + 10)] + newInside + text[closeIdx..];
                        modified = true;
                    }
                }

                // 2. Update body graph link in ## 来源与关联
                if (!string.IsNullOrWhiteSpace(newGraphLink) && !text.Contains(newGraphLink))
                {
                    if (text.Contains("## 来源与关联"))
                    {
                        var linkIdx = text.IndexOf("## 来源与关联");
                        var nextHeader = text.IndexOf("\n## ", linkIdx + 12);
                        var insertPos = nextHeader > linkIdx ? nextHeader : text.Length;
                        text = text.Insert(insertPos, $"\n- {newGraphLink}");
                        modified = true;
                    }
                }

                if (modified)
                {
                    await File.WriteAllTextAsync(filePath, text);
                }
            }
            catch { }
        }
    }
}

