using System;
using System.Collections.Generic;

namespace ObsidianGraphRag.Shared.Models
{
    public static class TheSchemaDefinitions
    {
        public const string TypeRaw = "raw";
        public const string TypeSource = "source";
        public const string TypeEntity = "entity";
        public const string TypeConcept = "concept";
        public const string TypeComparison = "comparison";
        public const string TypeOverview = "overview";

        public const string FolderRaw = "raw";
        public const string FolderWiki = "wiki";
        public const string FolderSources = "wiki/sources";
        public const string FolderEntities = "wiki/entities";
        public const string FolderConcepts = "wiki/concepts";
        public const string FolderComparisons = "wiki/comparisons";
        public const string FolderOverview = "wiki/overview";

        public const string FileIndex = "wiki/Wiki Index.md";
        public const string FileLog = "wiki/log.md";

        public static string GenerateSourceTemplate(
            string title,
            string sourcePath,
            string sourceType,
            string sourceUrl,
            string retrievedAt,
            string contentHash,
            string summary,
            string keyPoints,
            string relatedEntities,
            List<MediaResource>? mediaResources = null)
        {
            var cleanTitle = title.Replace("\"", "\\\"");
            var cleanSummary = summary.Replace("\"", "\\\"");
            var urlYaml = string.IsNullOrWhiteSpace(sourceUrl) ? "" : $"\nsource_url: \"{sourceUrl}\"";
            var retrievedYaml = string.IsNullOrWhiteSpace(retrievedAt) ? "" : $"\nretrieved_at: \"{retrievedAt}\"";

            var videos = mediaResources?.FindAll(m => m.Type == MediaResourceType.Video) ?? new List<MediaResource>();
            var images = mediaResources?.FindAll(m => m.Type == MediaResourceType.Image && m.Role != MediaResourceRole.Decorative) ?? new List<MediaResource>();

            var mediaYaml = "";
            if (videos.Count > 0 || images.Count > 0)
            {
                mediaYaml = $"\nhas_media: true\nvideo_count: {videos.Count}\nimage_count: {images.Count}";
            }

            var mediaSection = "";
            if (videos.Count > 0 || images.Count > 0)
            {
                mediaSection = "\n## Related Media\n\n";
                if (videos.Count > 0)
                {
                    mediaSection += "### Videos\n";
                    foreach (var v in videos)
                    {
                        var vTitle = string.IsNullOrWhiteSpace(v.Title) ? "YouTube Tutorial" : v.Title;
                        mediaSection += $"- [{vTitle}]({v.Url})\n";
                    }
                    mediaSection += "\n";
                }

                if (images.Count > 0)
                {
                    mediaSection += "### Images\n";
                    foreach (var img in images)
                    {
                        var alt = string.IsNullOrWhiteSpace(img.Alt) ? "Illustration" : img.Alt;
                        mediaSection += $"- ![{alt}]({img.Url})\n";
                    }
                    mediaSection += "\n";
                }
            }

            var sourceLinkSection = "";
            if (!string.IsNullOrWhiteSpace(sourceUrl))
            {
                sourceLinkSection = $"\n## Source\n[{title}]({sourceUrl})\n";
            }

            return $"---\n" +
                   $"type: source\n" +
                   $"title: \"{cleanTitle}\"\n" +
                   $"source_path: \"{sourcePath}\"\n" +
                   $"source_type: {sourceType}{urlYaml}{retrievedYaml}{mediaYaml}\n" +
                   $"content_hash: \"{contentHash}\"\n" +
                   $"updated: {DateTime.Now:yyyy-MM-dd}\n" +
                   $"summary: \"{cleanSummary}\"\n" +
                   $"---\n\n" +
                   $"# {title}\n\n" +
                   $"## 核心摘要\n{summary}\n\n" +
                   $"## 核心要点\n{keyPoints}\n\n" +
                   $"## 关联知识\n{relatedEntities}\n" +
                   $"{mediaSection}" +
                   $"{sourceLinkSection}";
        }

        public static string GenerateEntityTemplate(
            string entityName,
            string entityType,
            string summary,
            List<string> aliases,
            List<string> sources,
            string relations,
            List<string>? graphLinks = null)
        {
            var cleanName = entityName.Replace("\"", "\\\"");
            var cleanSummary = summary.Replace("\"", "\\\"");
            var cleanType = (entityType ?? "General").Replace("/", "").Replace("\\", "").Trim();
            if (string.IsNullOrWhiteSpace(cleanType)) cleanType = "General";

            var aliasYaml = aliases != null && aliases.Count > 0
                ? "aliases: [" + string.Join(", ", aliases.ConvertAll(a => $"\"{a.Replace("\"", "\\\"")}\"")) + "]"
                : "aliases: []";

            var sourcesYaml = sources != null && sources.Count > 0
                ? "sources: [" + string.Join(", ", sources.ConvertAll(s => $"\"{s}\"")) + "]"
                : "sources: []";

            var graphLinksSection = graphLinks != null && graphLinks.Count > 0
                ? string.Join("\n", graphLinks.ConvertAll(l => $"- {l}"))
                : "- 无特定关联";

            return $"---\n" +
                   $"type: entity\n" +
                   $"title: \"{cleanName}\"\n" +
                   $"entity_type: {cleanType}\n" +
                   $"{aliasYaml}\n" +
                   $"{sourcesYaml}\n" +
                   $"updated: {DateTime.Now:yyyy-MM-dd}\n" +
                   $"summary: \"{cleanSummary}\"\n" +
                   $"---\n\n" +
                   $"# {entityName}\n\n" +
                   $"## 概述\n{summary}\n\n" +
                   $"## 核心属性与流程\n{relations}\n\n" +
                   $"## 来源与关联 (Wiki Graph Nodes)\n{graphLinksSection}\n";
        }

        public static string GenerateConceptTemplate(
            string conceptName,
            string summary,
            List<string> aliases,
            List<string> sources,
            string details,
            List<string>? graphLinks = null)
        {
            var cleanName = conceptName.Replace("\"", "\\\"");
            var cleanSummary = summary.Replace("\"", "\\\"");

            var aliasYaml = aliases != null && aliases.Count > 0
                ? "aliases: [" + string.Join(", ", aliases.ConvertAll(a => $"\"{a.Replace("\"", "\\\"")}\"")) + "]"
                : "aliases: []";

            var sourcesYaml = sources != null && sources.Count > 0
                ? "sources: [" + string.Join(", ", sources.ConvertAll(s => $"\"{s}\"")) + "]"
                : "sources: []";

            var graphLinksSection = graphLinks != null && graphLinks.Count > 0
                ? string.Join("\n", graphLinks.ConvertAll(l => $"- {l}"))
                : "- 无特定关联";

            return $"---\n" +
                   $"type: concept\n" +
                   $"title: \"{cleanName}\"\n" +
                   $"{aliasYaml}\n" +
                   $"{sourcesYaml}\n" +
                   $"updated: {DateTime.Now:yyyy-MM-dd}\n" +
                   $"summary: \"{cleanSummary}\"\n" +
                   $"---\n\n" +
                   $"# {conceptName}\n\n" +
                   $"## 概念定义\n{summary}\n\n" +
                   $"## 作用机制与规则\n{details}\n\n" +
                   $"## 来源与关联 (Wiki Graph Nodes)\n{graphLinksSection}\n";
        }
    }
}
