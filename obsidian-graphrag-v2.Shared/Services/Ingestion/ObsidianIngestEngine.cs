using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ObsidianGraphRag.Shared.Models;
using ObsidianGraphRag.Shared.Services.Ingestion.Pipeline;
using ObsidianGraphRag.Shared.Services.LLM;
using ObsidianGraphRag.Shared.Services.Vault;

namespace ObsidianGraphRag.Shared.Services.Ingestion
{
    public class ObsidianIngestEngine
    {
        private static readonly JsonSerializerOptions ExtractionJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        private static readonly JsonElement ExtractionSchema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["summary", "key_points", "candidates"],
              "properties": {
                "summary": { "type": "string" },
                "key_points": { "type": "array", "items": { "type": "string" } },
                "candidates": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["name", "kind", "relation_type"],
                    "properties": {
                      "name": { "type": "string" },
                      "kind": { "type": "string", "enum": ["entity", "concept"] },
                      "entity_type": { "type": "string" },
                      "aliases": { "type": "array", "items": { "type": "string" } },
                      "summary": { "type": "string" },
                      "details": { "type": "string" },
                      "relation_type": { "type": "string", "enum": ["SAME_AS", "SUBCONCEPT_OF", "DOCUMENT", "RELATED_TO", "DISTINCT"] },
                      "target_canonical_name": { "type": "string" },
                      "parent_concepts": { "type": "array", "items": { "type": "string" } },
                      "published_by": { "type": "string" }
                    }
                  }
                }
              }
            }
            """);

        private readonly VaultGraphEngine _graphEngine;
        private readonly ILlmClient _llmClient;
        private readonly object _jobLock = new();

        public IngestJobStatus CurrentJob { get; private set; } = new();
        public event Action? OnProgressChanged;
        public event Action? OnSourceArchiveChanged;

        public ObsidianIngestEngine(VaultGraphEngine graphEngine, ILlmClient llmClient)
        {
            _graphEngine = graphEngine;
            _llmClient = llmClient;
        }

        internal void SetSourceArchived(string site, string? documentId, bool archived)
        {
            if (!IsDirectoryName(site) || (site == DocumentImportService.Collection) != (documentId is not null) ||
                (documentId is not null && !IsDirectoryName(documentId)))
                throw new ArgumentException("Invalid source.");

            lock (_jobLock)
            {
                if (CurrentJob.IsRunning)
                    throw new InvalidOperationException("Wait for ingestion to finish before changing the archive.");

                var directory = Path.Combine(_graphEngine.RootPath, "raw", site);
                SourceArchive.SetArchived(documentId is null ? directory : Path.Combine(directory, documentId), archived);
            }
            OnSourceArchiveChanged?.Invoke();
        }

        public async Task RunIngestAsync(string siteName, string targetName, string? documentId = null, bool reprocess = false)
        {
            var vaultPath = _graphEngine.RootPath;
            lock (_jobLock)
            {
                if (CurrentJob.IsRunning)
                    return;

                CurrentJob = new IngestJobStatus
                {
                    IsRunning = true,
                    TargetName = targetName,
                    Reprocess = reprocess,
                    CurrentStepDescription = "Preparing ingestion"
                };
            }

            try
            {
                AddLog("INFO", $"Starting {(reprocess ? "reprocess" : "ingestion")} for {targetName}.");
                NotifyProgress();
                if (!IsDirectoryName(siteName) ||
                    (documentId is not null && (siteName != DocumentImportService.Collection || !IsDirectoryName(documentId))))
                    throw new ArgumentException("Invalid ingestion source.");
                var rawSiteDir = Path.Combine(vaultPath, "raw", siteName);
                var inputDirectory = documentId is null ? rawSiteDir : Path.Combine(rawSiteDir, documentId);
                var archive = SourceArchive.Read(vaultPath);
                var rawFiles = Directory.Exists(inputDirectory)
                    ? Directory.EnumerateFiles(inputDirectory, "*.md", SearchOption.AllDirectories)
                        .Where(file => !archive.ContainsPath(Path.GetRelativePath(vaultPath, file))).ToArray()
                    : [];
                Array.Sort(rawFiles, StringComparer.Ordinal);
                if (rawFiles.Length == 0)
                {
                    AddLog("WARN", $"No Markdown found for {targetName}. Refresh the source list and choose an available source.");
                    CurrentJob.CurrentStepDescription = "No Markdown to ingest";
                    return;
                }

                CurrentJob.TotalFiles = rawFiles.Length;
                AddLog("INFO", $"Found {rawFiles.Length} Markdown files for {targetName}.");
                NotifyProgress();

                // Ensure destination directories exist
                Directory.CreateDirectory(Path.Combine(vaultPath, "wiki", "sources", siteName));
                Directory.CreateDirectory(Path.Combine(vaultPath, "wiki", "entities"));
                Directory.CreateDirectory(Path.Combine(vaultPath, "wiki", "concepts"));
                Directory.CreateDirectory(Path.Combine(vaultPath, "wiki", "comparisons"));
                Directory.CreateDirectory(Path.Combine(vaultPath, "wiki", "overview"));

                // Load the schema and open an audit session for the collection.
                var schemaResult = await SchemaLoader.LoadAsync(vaultPath);
                AddLog("INFO", $"📄 Loaded TheSchema.md (Hash: {schemaResult.SchemaHash})");
                await AuditLogger.AppendSessionHeaderAsync(vaultPath, siteName, schemaResult.SchemaHash);

                // 3. Build In-Memory Unified KnowledgeCatalog (Entities + Concepts + Aliases)
                CurrentJob.CurrentStepDescription = "Building In-Memory Knowledge Catalog...";
                NotifyProgress();

                var catalog = await BuildKnowledgeCatalogAsync(vaultPath);
                AddLog("INFO", $"🧠 Initialized Knowledge Catalog with {catalog.Count} existing knowledge nodes");

                if (reprocess)
                {
                    foreach (var rawFilePath in rawFiles)
                    {
                        var subpath = GetWikiSourceSubpath(Path.GetRelativePath(rawSiteDir, rawFilePath));
                        var pendingPath = Path.Combine(vaultPath, "wiki", "sources", siteName, subpath) + ".pending";
                        Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
                        await File.WriteAllTextAsync(pendingPath, string.Empty);
                    }
                }

                // 4. Process Each Raw Document
                foreach (var rawFilePath in rawFiles)
                {
                    // Mirror relative subpath (e.g. "treatments/acne.md" or "blogs-acne-skin.md")
                    var relativeRawSubPath = GetWikiSourceSubpath(Path.GetRelativePath(rawSiteDir, rawFilePath));
                    var subPathNoExt = relativeRawSubPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        ? relativeRawSubPath[..^3]
                        : relativeRawSubPath;

                    CurrentJob.CurrentStepDescription = $"Checking: {relativeRawSubPath}";
                    CurrentJob.ActiveSection = relativeRawSubPath;
                    NotifyProgress();

                    var rawContent = await File.ReadAllTextAsync(rawFilePath);
                    var rawNote = ParseTempRawNote(rawFilePath, rawContent);

                    // Destination mirrored source file
                    var mirroredSourceFilePath = Path.Combine(vaultPath, "wiki", "sources", siteName, relativeRawSubPath);
                    var retryMarker = mirroredSourceFilePath + ".pending";
                    Directory.CreateDirectory(Path.GetDirectoryName(mirroredSourceFilePath)!);

                    string? existingSourceHash = null;
                    if (File.Exists(mirroredSourceFilePath))
                    {
                        var existingSourceContent = await File.ReadAllTextAsync(mirroredSourceFilePath);
                        var existingSourceNote = ParseTempRawNote(mirroredSourceFilePath, existingSourceContent);
                        existingSourceHash = existingSourceNote.Frontmatter.TryGetValue("content_hash", out var ch) ? ch?.ToString() : existingSourceNote.Hash;
                    }

                    // 5. Hash-based Idempotency Check (ChangeDetector)
                    var changeResult = ChangeDetector.Evaluate(rawContent, existingSourceHash, reprocess || File.Exists(retryMarker));
                    if (!changeResult.IsChanged)
                    {
                        CurrentJob.UnchangedFiles++;
                        CurrentJob.ProcessedFiles++;
                        AddLog("INFO", $"⚡ [Unchanged - 0 Tokens] {relativeRawSubPath} (Hash: {changeResult.ContentHash})");
                        await AuditLogger.AppendEntryAsync(vaultPath, siteName, new AuditLogEntry
                        {
                            SourcePath = $"raw/{siteName}/{relativeRawSubPath}",
                            Action = "SKIPPED",
                            SkipReason = "unchanged"
                        });
                        CurrentJob.ActiveSection = null;
                        NotifyProgress();
                        continue;
                    }

                    // Keep interrupted reprocessing eligible even if an older compiled copy has the same hash.
                    if (!File.Exists(retryMarker))
                        await File.WriteAllTextAsync(retryMarker, string.Empty);

                    // 6. Source Preprocessor & Content Classification
                    var prepResult = SourcePreprocessor.Evaluate(rawFilePath);
                    if (!prepResult.Extractable)
                    {
                        AddLog("INFO", $"⏭️ [Skipped Non-Knowledge Page] {relativeRawSubPath} ({prepResult.SkipReason})");

                        // Write placeholder source summary
                        var placeholderMarkdown = TheSchemaDefinitions.GenerateSourceTemplate(
                            rawNote.Title,
                            $"raw/{siteName}/{relativeRawSubPath}",
                            "web",
                            GetSourceUrl(rawNote),
                            GetCrawlTime(rawNote),
                            changeResult.ContentHash,
                            $"系统操作/导航页面 ({prepResult.SkipReason})",
                            "- 属于非知识性功能页，不参与实体提取",
                            "- 无业务实体"
                        );
                        await WriteSourceAsync(mirroredSourceFilePath, placeholderMarkdown);

                        await AuditLogger.AppendEntryAsync(vaultPath, siteName, new AuditLogEntry
                        {
                            SourcePath = $"raw/{siteName}/{relativeRawSubPath}",
                            Action = "SKIPPED",
                            SkipReason = prepResult.SkipReason
                        });
                        File.Delete(retryMarker);
                        CurrentJob.UpdatedFiles++;
                        CurrentJob.ProcessedFiles++;
                        CurrentJob.ActiveSection = null;
                        NotifyProgress();
                        continue;
                    }

                    // 7. Execute Knowledge Extraction Pipeline
                    AddLog("INFO", $"⚙️ Processing: {relativeRawSubPath} ...");
                    AuditLogEntry auditEntry;
                    try
                    {
                        auditEntry = await ProcessKnowledgeDocumentAsync(
                            vaultPath, siteName, relativeRawSubPath, subPathNoExt, mirroredSourceFilePath,
                            rawNote, rawContent, changeResult.ContentHash, schemaResult.SchemaContent, catalog);
                    }
                    catch (KnowledgeExtractionException error)
                    {
                        CurrentJob.SectionsToRetry.Enqueue(relativeRawSubPath);
                        AddLog("WARN", $"{relativeRawSubPath} still needs extraction. {error.Message} Continuing with the next section.");
                        await AuditLogger.AppendEntryAsync(vaultPath, siteName, new AuditLogEntry
                        {
                            SourcePath = $"raw/{siteName}/{relativeRawSubPath}",
                            Action = "FAILED",
                            ContentHash = changeResult.ContentHash,
                            Warnings = { error.Message }
                        });
                        CurrentJob.ProcessedFiles++;
                        CurrentJob.ActiveSection = null;
                        NotifyProgress();
                        continue;
                    }

                    await AuditLogger.AppendEntryAsync(vaultPath, siteName, auditEntry);
                    File.Delete(retryMarker);
                    CurrentJob.UpdatedFiles++;
                    CurrentJob.ProcessedFiles++;
                    CurrentJob.ActiveSection = null;
                    NotifyProgress();
                }

                // 8. Update index.md (Human MOC & Site Indexes)
                await UpdateWikiIndexAsync(vaultPath, siteName);

                _graphEngine.Reload();
                var remaining = CurrentJob.SectionsToRetry.Count;
                CurrentJob.CurrentStepDescription = remaining == 0 ? "Completed" : "Completed with sections to retry";
                var retryNote = remaining == 0 ? "" : $" {remaining} {(remaining == 1 ? "section needs" : "sections need")} retrying.";
                AddLog(remaining == 0 ? "SUCCESS" : "WARN", $"Ingestion complete. {CurrentJob.UpdatedFiles} compiled, {CurrentJob.UnchangedFiles} unchanged.{retryNote} Graph reloaded.");
            }
            catch (Exception ex)
            {
                CurrentJob.CurrentStepDescription = "Stopped before completion";
                CurrentJob.FailedSection = CurrentJob.ActiveSection;
                var location = CurrentJob.FailedSection is { } section ? $" in {section}" : "";
                AddLog("ERROR", $"Ingestion error{location}: {ex.Message}");
            }
            finally
            {
                lock (_jobLock)
                    CurrentJob.IsRunning = false;
                NotifyProgress();
            }
        }

        private static bool IsDirectoryName(string value) =>
            !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
            !Path.IsPathRooted(value) && !value.Contains('/') && !value.Contains('\\');

        internal static string GetWikiSourceSubpath(string relativeRawPath) =>
            relativeRawPath.Equals("index.md", StringComparison.OrdinalIgnoreCase)
                ? "home.md" : relativeRawPath.Replace('\\', '/');

        private async Task<ExtractionResult> ExtractKnowledgeAsync(string systemPrompt, Func<string> retryPrompt,
            string userPrompt, string section)
        {
            for (var attempt = 0; ; attempt++)
            {
                var prompt = attempt == 0 ? systemPrompt : retryPrompt();
                var json = await _llmClient.ExtractStructuredJsonAsync(prompt, userPrompt, ExtractionSchema);
                try
                {
                    if (json.StartsWith("[LLM Output Truncated]", StringComparison.Ordinal))
                        throw new JsonException("The model reached its generation limit before completing the extraction.");
                    if (json.StartsWith("[LLM ", StringComparison.Ordinal))
                        throw new InvalidOperationException(json);

                    var result = JsonSerializer.Deserialize<ExtractionResult>(json, ExtractionJsonOptions);
                    if (result?.Candidates is null || result.Candidates.Any(candidate => candidate?.Name is null))
                        throw new JsonException("The model did not return a valid list of knowledge candidates.");
                    return result;
                }
                catch (JsonException error) when (attempt == 0)
                {
                    AddLog("WARN", $"The model returned incomplete or invalid JSON for {section}. Retrying extraction. {error.Message}");
                }
                catch (JsonException error)
                {
                    throw new KnowledgeExtractionException($"The model returned incomplete or invalid JSON after retrying. {error.Message}", error);
                }
            }
        }

        private sealed record ExtractionResult(string? Summary, string?[]? KeyPoints, ExtractedCandidate[]? Candidates);

        private sealed record ExtractedCandidate(string? Name, string? Kind, string? EntityType, string?[]? Aliases,
            string? Summary, string? Details, string? RelationType, string? TargetCanonicalName,
            string?[]? ParentConcepts, string? PublishedBy);

        private sealed class KnowledgeExtractionException(string message, Exception inner) : Exception(message, inner);

        private async Task<AuditLogEntry> ProcessKnowledgeDocumentAsync(
            string vaultPath,
            string siteName,
            string relativeRawSubPath,
            string subPathNoExt,
            string mirroredSourceFilePath,
            ObsidianNote rawNote,
            string content,
            string contentHash,
            string schemaPromptContext,
            KnowledgeCatalog catalog)
        {
            var auditEntry = new AuditLogEntry
            {
                SourcePath = $"sources/{siteName}/{subPathNoExt}",
                Action = File.Exists(mirroredSourceFilePath) ? "UPDATED" : "NEW",
                ContentHash = contentHash
            };

            var currentRawRelPath = $"raw/{siteName}/{relativeRawSubPath}";
            var currentSourceGraphLink = $"[[sources/{siteName}/{subPathNoExt}]]";

            string summary = rawNote.Title;
            string keyPointsStr = "- 包含核心业务介绍与操作指引";
            var linkedEntities = new List<string>();

            // 1. Decoupled Media Extraction: Separate media metadata from core knowledge extraction
            var mediaResult = MediaExtractor.Extract(content);
            if (mediaResult.Media.Count > 0)
            {
                var vCount = mediaResult.Media.Count(m => m.Type == MediaResourceType.Video);
                var iCount = mediaResult.Media.Count(m => m.Type == MediaResourceType.Image);
                AddLog("INFO", $"  📎 Extracted {mediaResult.Media.Count} media resources ({vCount} videos, {iCount} images) as Source metadata");
            }

            // [TECH DEBT - Next Phase]: Replace fixed 3000-char substring with token-aware / section-aware content selection.
            // Media URLs have been replaced with concise semantic placeholders ([Image: Alt], [Video: Title])
            // to maximize usable knowledge extraction context and prevent URL token waste.
            var sanitizedContent = mediaResult.SanitizedContentForLlm;
            var trimmed = sanitizedContent.Length > 3000 ? sanitizedContent[..3000] : sanitizedContent;
            var archive = SourceArchive.Read(vaultPath);
            var richCatalogContext = catalog.GetRichCatalogContext(100, entry => !entry.Sources.Any(archive.ContainsPath));
            string BuildPrompt(string catalogJson) => $@"You are a production-grade knowledge engineering compiler adhering strictly to TheSchema.md conventions.
Ontology & Linking Rules:
{schemaPromptContext}

Existing Knowledge Catalog (Active Entities & Concepts in Vault):
{catalogJson}

CRITICAL 4-WAY DECISION RULES:
1. SAME_AS (STRICT SYNONYM ONLY):
   - Use SAME_AS ONLY when the candidate is 100% linguistically identical or an exact acronym/translation/spelling variant of an existing canonical node.
     * Examples: 'e-Invoicing' -> 'e-Invoice', 'Electronic Invoicing' -> 'e-Invoice', 'LHDN' -> 'Inland Revenue Board of Malaysia'.
   - NEVER use SAME_AS for sub-rules, subtypes, workflows, exemptions, products, or people!
     * 'Consolidated e-Invoice', 'Self-billed e-Invoice', 'e-Invoice Exemption' are NOT SAME_AS 'e-Invoice' -> MUST use SUBCONCEPT_OF!
     * 'Cloud-Based POS', 'Hybrid POS', 'Offline Mode' are NOT SAME_AS 'POS System' -> MUST use SUBCONCEPT_OF or DISTINCT concept!
     * Founders, speakers, or personnel (e.g. 'Bryan Cheong', 'Shermaine') are NOT SAME_AS company 'EBI Software' -> MUST use kind: 'entity', entity_type: 'Person'!
     * Products or apps (e.g. 'memberLO', 'SQL EBI POS', 'imin Swift 2 Pro') are NOT SAME_AS company 'EBI Software' -> MUST use kind: 'entity', entity_type: 'Product'!
2. SUBCONCEPT_OF (Specialized Sub-rules / Types / Workflows):
   - If a candidate is a specialized rule, subtype, process, requirement, workflow, or exemption belonging under a broader parent concept:
     kind: 'concept'
     relation_type: 'SUBCONCEPT_OF'
     target_canonical_name: '<exact title of parent concept>'
     parent_concepts: ['<exact title of parent concept>']
3. DOCUMENT ENTITY (Official Guidelines, Acts & Regulations):
   - Specific official guidelines, standards, acts, or regulations (e.g., 'e-Invoice Guideline v2.1', 'Income Tax Act 1967') are Document entities, NOT abstract concepts:
     kind: 'entity'
     entity_type: 'Document'
     relation_type: 'DOCUMENT'
     published_by: '<Publishing agency if known, e.g. Inland Revenue Board of Malaysia>'
     target_canonical_name: '<The core concept it regulates, e.g. e-Invoice>'
4. RELATED_TO / DISTINCT:
   - Independent organizations, people, products, services, or distinct business concepts:
     relation_type: 'RELATED_TO' or 'DISTINCT'

NOISE FILTERING:
- NEVER extract UI components, shopping cart, promo banners, rating stars (★★★★★), or generic navigation labels.
- NEVER extract media placeholders (e.g. [Image: ...], [Video: ...]) as Entity or Concept nodes. Media resources are handled separately as Source metadata.

Return one complete JSON object containing summary, key_points, and candidates.
Keep the summary to one sentence. Use short takeaways and distinct candidates supported by the source.
Candidates use a canonical name and the kind and relation_type from the rules above.
Use entity_type for the category and include aliases, details, target_canonical_name, parent_concepts,
or published_by when relevant.";

            var userPrompt = $"Title: {rawNote.Title}\nContent:\n{trimmed}";
            var extraction = await ExtractKnowledgeAsync(
                BuildPrompt(JsonSerializer.Serialize(richCatalogContext)),
                () => BuildPrompt(JsonSerializer.Serialize(richCatalogContext.Take(20))) +
                    "\nKeep every field brief. Omit duplicate candidates and finish the entire JSON object.",
                userPrompt, relativeRawSubPath);

            if (!string.IsNullOrWhiteSpace(extraction.Summary))
                summary = extraction.Summary;

            if (extraction.KeyPoints is { } points)
            {
                var bullets = points.Where(point => !string.IsNullOrWhiteSpace(point)).Select(point => $"- {point}").ToList();
                if (bullets.Count > 0)
                    keyPointsStr = string.Join("\n", bullets);
            }

            var candidates = new List<KnowledgeNodeCandidate>();
            foreach (var extracted in extraction.Candidates!)
            {
                var cName = extracted.Name!;
                var valRes = EntityValidator.Validate(cName);
                if (!valRes.IsValid)
                {
                    auditEntry.SkippedCandidates.Add($"{cName} ({valRes.RejectionReason})");
                    continue;
                }

                var candidate = new KnowledgeNodeCandidate
                {
                    Name = valRes.CleanedName,
                    Kind = extracted.Kind ?? "entity",
                    EntityType = extracted.EntityType ?? "General",
                    Summary = extracted.Summary ?? "",
                    Details = extracted.Details ?? "",
                    RelationType = extracted.RelationType ?? KnowledgeRelationTypes.Distinct,
                    TargetCanonicalName = extracted.TargetCanonicalName,
                    PublishedBy = extracted.PublishedBy,
                    Aliases = (extracted.Aliases ?? []).OfType<string>().Where(value => !string.IsNullOrEmpty(value)).ToList(),
                    ParentConcepts = (extracted.ParentConcepts ?? []).OfType<string>().Where(value => !string.IsNullOrEmpty(value)).ToList()
                };

                candidates.Add(candidate);
            }

            foreach (var candidate in candidates)
            {
                var res = await EntityResolutionService.ResolveKnowledgeCandidateAsync(
                    vaultPath, candidate, currentRawRelPath, currentSourceGraphLink, catalog);

                if (!string.IsNullOrEmpty(res.WikiLink))
                {
                    linkedEntities.Add(res.WikiLink);
                    switch (res.ActionTaken)
                    {
                        case "ALIASED":
                            auditEntry.ReusedEntities.Add($"{candidate.Name} (aliased into [[{res.CanonicalName}]])");
                            break;
                        case "SUBCONCEPT_CREATED":
                            CurrentJob.NewConceptsCreated++;
                            auditEntry.AssociatedConcepts.Add($"{res.CanonicalName} (subconcept of {candidate.TargetCanonicalName ?? candidate.ParentConcepts.FirstOrDefault()})");
                            break;
                        case "DOCUMENT_CREATED":
                            CurrentJob.NewEntitiesCreated++;
                            auditEntry.CreatedEntities.Add($"{res.CanonicalName} [Document]");
                            break;
                        case "CONCEPT_CREATED":
                            CurrentJob.NewConceptsCreated++;
                            auditEntry.AssociatedConcepts.Add(res.CanonicalName);
                            break;
                        case "ENTITY_CREATED":
                            CurrentJob.NewEntitiesCreated++;
                            auditEntry.CreatedEntities.Add(res.CanonicalName);
                            break;
                        case "REUSED":
                        default:
                            auditEntry.ReusedEntities.Add(res.CanonicalName);
                            break;
                    }
                }
            }

            // 3. Compile Mirrored Source Markdown Note
            var sourceUrl = GetSourceUrl(rawNote);
            var crawlTime = GetCrawlTime(rawNote);
            var distinctEntities = linkedEntities.Distinct().ToList();

            var sourceMarkdown = TheSchemaDefinitions.GenerateSourceTemplate(
                rawNote.Title,
                currentRawRelPath,
                "web",
                sourceUrl,
                crawlTime,
                contentHash,
                summary,
                keyPointsStr,
                distinctEntities.Count > 0 ? string.Join("\n", distinctEntities.Select(e => $"- {e}")) : "- 无独立关联知识",
                mediaResult.Media
            );

            await WriteSourceAsync(mirroredSourceFilePath, sourceMarkdown);
            AddLog("SUCCESS", $"  -> Compiled sources/{siteName}/{subPathNoExt}.md ({distinctEntities.Count} links)");

            return auditEntry;
        }

        private static async Task WriteSourceAsync(string path, string content)
        {
            var temporaryPath = path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, content);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        private static async Task<KnowledgeCatalog> BuildKnowledgeCatalogAsync(string vaultPath)
        {
            var catalog = new KnowledgeCatalog();

            // 1. Load entities
            var entitiesDir = Path.Combine(vaultPath, "wiki", "entities");
            if (Directory.Exists(entitiesDir))
            {
                foreach (var file in Directory.GetFiles(entitiesDir, "*.md"))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(file);
                        var note = ParseTempRawNote(file, content);
                        var entry = new KnowledgeCatalogEntry
                        {
                            Title = Path.GetFileNameWithoutExtension(file),
                            Kind = "entity",
                            FilePath = file,
                            Summary = note.Summary,
                            Sources = note.Sources
                        };

                        if (note.Frontmatter.TryGetValue("entity_type", out var et) && et != null)
                            entry.EntityType = et.ToString() ?? "General";
                        else if (note.Frontmatter.TryGetValue("type", out var t) && t != null)
                            entry.EntityType = t.ToString() ?? "General";

                        if (note.Frontmatter.TryGetValue("aliases", out var alObj))
                        {
                            if (alObj is IEnumerable<string> strList) entry.Aliases = strList.ToList();
                            else if (alObj is IEnumerable<object> objList) entry.Aliases = objList.Select(o => o.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                        }

                        catalog.Register(entry);
                    }
                    catch { }
                }
            }

            // 2. Load concepts
            var conceptsDir = Path.Combine(vaultPath, "wiki", "concepts");
            if (Directory.Exists(conceptsDir))
            {
                foreach (var file in Directory.GetFiles(conceptsDir, "*.md"))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(file);
                        var note = ParseTempRawNote(file, content);
                        var entry = new KnowledgeCatalogEntry
                        {
                            Title = Path.GetFileNameWithoutExtension(file),
                            Kind = "concept",
                            FilePath = file,
                            Summary = note.Summary,
                            Sources = note.Sources
                        };

                        if (note.Frontmatter.TryGetValue("aliases", out var alObj))
                        {
                            if (alObj is IEnumerable<string> strList) entry.Aliases = strList.ToList();
                            else if (alObj is IEnumerable<object> objList) entry.Aliases = objList.Select(o => o.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                        }

                        catalog.Register(entry);
                    }
                    catch { }
                }
            }

            return catalog;
        }

        private static string GetSourceUrl(ObsidianNote note)
        {
            if (note.Frontmatter.TryGetValue("url", out var u1) && u1 != null) return u1.ToString() ?? "";
            if (note.Frontmatter.TryGetValue("source_url", out var u2) && u2 != null) return u2.ToString() ?? "";
            if (note.Frontmatter.TryGetValue("source", out var u3) && u3 != null) return u3.ToString() ?? "";
            return note.Sources.Count > 0 ? note.Sources[0] : "";
        }

        private static string GetCrawlTime(ObsidianNote note)
        {
            if (note.Frontmatter.TryGetValue("retrieved_at", out var ra) && ra != null) return ra.ToString() ?? DateTime.Now.ToString("s");
            if (note.Frontmatter.TryGetValue("crawl_time", out var ct) && ct != null) return ct.ToString() ?? DateTime.Now.ToString("s");
            return DateTime.Now.ToString("s");
        }

        private static ObsidianNote ParseTempRawNote(string filePath, string content)
        {
            var note = new ObsidianNote { FilePath = filePath, Title = Path.GetFileNameWithoutExtension(filePath) };
            if (content.StartsWith("---"))
            {
                int end = content.IndexOf("---", 3, StringComparison.Ordinal);
                if (end > 0)
                {
                    var lines = content.Substring(3, end - 3).Split('\n');
                    foreach (var l in lines)
                    {
                        var parts = l.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            var k = parts[0].Trim().ToLower();
                            var v = parts[1].Trim();
                            if (k == "title") note.Title = v.Trim('\"', '\'');
                            if (k == "hash" || k == "content_hash") note.Hash = v.Trim('\"', '\'');
                            if (k == "summary") note.Summary = v.Trim('\"', '\'');
                            if (k == "entity_type" || k == "type") note.Frontmatter[k] = v.Trim('\"', '\'');

                            if (k == "aliases")
                            {
                                var rawList = v.Trim();
                                if (rawList.StartsWith("[") && rawList.EndsWith("]"))
                                {
                                    var items = rawList.Trim('[', ']').Split(',');
                                    var list = new List<string>();
                                    foreach (var it in items)
                                    {
                                        var clean = it.Trim().Trim('\"', '\'');
                                        if (!string.IsNullOrEmpty(clean)) list.Add(clean);
                                    }
                                    note.Frontmatter["aliases"] = list;
                                }
                            }

                            if (k == "sources" || k == "source" || k == "url" || k == "source_url" || k == "source_path")
                            {
                                if (v.StartsWith("[") && v.EndsWith("]"))
                                {
                                    var items = v.Trim('[', ']').Split(',');
                                    foreach (var it in items)
                                    {
                                        var clean = it.Trim().Trim('\"', '\'');
                                        if (!string.IsNullOrEmpty(clean) && !note.Sources.Contains(clean))
                                            note.Sources.Add(clean);
                                    }
                                }
                                else
                                {
                                    var clean = v.Trim('\"', '\'');
                                    if (!string.IsNullOrEmpty(clean) && !note.Sources.Contains(clean))
                                        note.Sources.Add(clean);
                                }
                            }
                        }
                    }
                }
            }
            return note;
        }

        private async Task UpdateWikiIndexAsync(string vaultPath, string currentSite = "")
        {
            _graphEngine.Reload();

            string CleanVaultLink(string path)
            {
                var p = path.Replace('\\', '/');
                if (p.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase))
                    p = p[5..];
                if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    p = p[..^3];
                return p;
            }

            // 1. Detect all active sites
            var sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourcesDir = Path.Combine(vaultPath, "wiki", "sources");
            if (Directory.Exists(sourcesDir))
            {
                foreach (var dir in Directory.GetDirectories(sourcesDir))
                {
                    var sName = Path.GetFileName(dir);
                    if (!sName.StartsWith(".") && !sName.StartsWith("_"))
                        sites.Add(sName);
                }
            }
            if (!string.IsNullOrWhiteSpace(currentSite)) sites.Add(currentSite);

            var siteStats = new List<(string Site, int SourceCount, int EntityCount)>();

            // 2. Generate Site-Specific Index for each website (sources/{site}/{site} Index.md)
            foreach (var site in sites)
            {
                var siteFolder = Path.Combine(sourcesDir, site);
                Directory.CreateDirectory(siteFolder);

                var siteIndexPath = Path.Combine(siteFolder, $"{site} Index.md");
                var legacyIndex = Path.Combine(siteFolder, "index.md");
                if (File.Exists(legacyIndex)) File.Delete(legacyIndex);

                var sitePrefix = $"wiki/sources/{site.ToLower()}/";

                var siteSources = _graphEngine.Notes.Values
                    .Where(n => n.NoteType == TheSchemaDefinitions.TypeSource &&
                                n.RelativePath.Replace('\\', '/').ToLower().StartsWith(sitePrefix) &&
                                !n.NoteName.EndsWith("Index", StringComparison.OrdinalIgnoreCase) &&
                                !n.NoteName.Equals("index", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n.RelativePath)
                    .ToList();

                var siteEntities = _graphEngine.Notes.Values
                    .Where(n => n.NoteType == TheSchemaDefinitions.TypeEntity &&
                                (n.Sources.Any(src => src.Contains($"/{site}/", StringComparison.OrdinalIgnoreCase) || src.StartsWith($"raw/{site}/", StringComparison.OrdinalIgnoreCase)) ||
                                 _graphEngine.Get1HopNeighbors(n.RelativePath).Any(nbr => nbr.RelativePath.Replace('\\', '/').ToLower().StartsWith(sitePrefix))))
                    .OrderBy(n => n.Title)
                    .ToList();

                var siteConcepts = _graphEngine.Notes.Values
                    .Where(n => n.NoteType == TheSchemaDefinitions.TypeConcept &&
                                (n.Sources.Any(src => src.Contains($"/{site}/", StringComparison.OrdinalIgnoreCase) || src.StartsWith($"raw/{site}/", StringComparison.OrdinalIgnoreCase)) ||
                                 _graphEngine.Get1HopNeighbors(n.RelativePath).Any(nbr => nbr.RelativePath.Replace('\\', '/').ToLower().StartsWith(sitePrefix))))
                    .OrderBy(n => n.Title)
                    .ToList();

                siteStats.Add((site, siteSources.Count, siteEntities.Count));

                var siteSb = new System.Text.StringBuilder();
                siteSb.AppendLine($"# {site} 知识地图 (Site Index)\n");
                siteSb.AppendLine($"> 🌐 站点名称: `{site}` | 📚 来源文档: {siteSources.Count} 篇 | 🏢 涉及实体: {siteEntities.Count} 个 | 🕒 最后同步: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");

                siteSb.AppendLine("## 📚 本站网页与文章 (Sources)");
                if (siteSources.Count == 0) siteSb.AppendLine("- 暂无来源文档");
                foreach (var s in siteSources)
                {
                    siteSb.AppendLine($"- [[{CleanVaultLink(s.RelativePath)}]] — {s.Summary}");
                }

                siteSb.AppendLine("\n## 🏢 本站涉及的业务实体 (Entities)");
                if (siteEntities.Count == 0) siteSb.AppendLine("- 暂未提取实体");
                foreach (var e in siteEntities)
                {
                    siteSb.AppendLine($"- [[{CleanVaultLink(e.RelativePath)}]] — {e.Summary}");
                }

                siteSb.AppendLine("\n## 💡 本站涉及的技术与原理 (Concepts)");
                if (siteConcepts.Count == 0) siteSb.AppendLine("- 暂无关联概念");
                foreach (var c in siteConcepts)
                {
                    siteSb.AppendLine($"- [[{CleanVaultLink(c.RelativePath)}]] — {c.Summary}");
                }

                await File.WriteAllTextAsync(siteIndexPath, siteSb.ToString(), System.Text.Encoding.UTF8);
                AddLog("INFO", $"📑 Generated Site Index: wiki/sources/{site}/{site} Index.md");
            }

            // 3. Generate Global Hub: wiki/Wiki Index.md
            var globalIndexPath = Path.Combine(vaultPath, "wiki", "Wiki Index.md");
            var legacyGlobalIndex = Path.Combine(vaultPath, "wiki", "index.md");
            if (File.Exists(legacyGlobalIndex)) File.Delete(legacyGlobalIndex);

            var globalSb = new System.Text.StringBuilder();
            globalSb.AppendLine("# Wiki Knowledge Index (Global MOC)\n");
            globalSb.AppendLine($"> 🌐 全局知识网络 | 涵盖站点: {sites.Count} 个 | 最后同步: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");

            globalSb.AppendLine("## 🌐 站点专属索引 (Site Indexes)");
            foreach (var stat in siteStats)
            {
                globalSb.AppendLine($"- [[sources/{stat.Site}/{stat.Site} Index]] — **{stat.Site}** 知识大纲 ({stat.SourceCount} 篇来源文档, {stat.EntityCount} 个业务实体)");
            }

            globalSb.AppendLine("\n## 🏢 全局业务实体库 (Global Entities)");
            var allEntities = _graphEngine.Notes.Values.Where(n => n.NoteType == TheSchemaDefinitions.TypeEntity).OrderBy(n => n.Title).ToList();
            if (allEntities.Count == 0) globalSb.AppendLine("- 暂无实体");
            foreach (var e in allEntities)
            {
                globalSb.AppendLine($"- [[{CleanVaultLink(e.RelativePath)}]] — {e.Summary}");
            }

            globalSb.AppendLine("\n## 💡 全局抽象概念库 (Global Concepts)");
            var allConcepts = _graphEngine.Notes.Values.Where(n => n.NoteType == TheSchemaDefinitions.TypeConcept).OrderBy(n => n.Title).ToList();
            if (allConcepts.Count == 0) globalSb.AppendLine("- 暂无概念");
            foreach (var c in allConcepts)
            {
                globalSb.AppendLine($"- [[{CleanVaultLink(c.RelativePath)}]] — {c.Summary}");
            }

            await File.WriteAllTextAsync(globalIndexPath, globalSb.ToString(), System.Text.Encoding.UTF8);
            AddLog("SUCCESS", $"🎉 Updated Global Index: wiki/Wiki Index.md");
        }

        private void AddLog(string level, string message)
        {
            CurrentJob.Logs.Enqueue(new IngestLogEntry { Level = level, Message = message, Timestamp = DateTime.Now });
        }

        private void NotifyProgress() => OnProgressChanged?.Invoke();
    }
}
