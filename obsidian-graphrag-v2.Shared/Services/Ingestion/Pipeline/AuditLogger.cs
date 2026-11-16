using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ObsidianGraphRag.Shared.Services.Ingestion.Pipeline
{
    public class AuditLogEntry
    {
        public string SourcePath { get; set; } = string.Empty;
        public string Action { get; set; } = "UPDATED"; // "NEW", "UPDATED", "SKIPPED"
        public string? ContentHash { get; set; }
        public string? SkipReason { get; set; }
        public List<string> CreatedEntities { get; set; } = new();
        public List<string> ReusedEntities { get; set; } = new();
        public List<string> AssociatedConcepts { get; set; } = new();
        public List<string> SkippedCandidates { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public static class AuditLogger
    {
        public static async Task AppendSessionHeaderAsync(string vaultPath, string siteName, string schemaHash)
        {
            var header = $"\n## [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Ingestion Run | site: {siteName} | schema_hash: {schemaHash}\n\n";

            // 1. Global log: wiki/log.md
            var globalLogPath = Path.Combine(vaultPath, "wiki", "log.md");
            await File.AppendAllTextAsync(globalLogPath, header, Encoding.UTF8);

            // 2. Site-specific log: wiki/sources/{site}/log.md
            if (!string.IsNullOrWhiteSpace(siteName))
            {
                var siteLogDir = Path.Combine(vaultPath, "wiki", "sources", siteName);
                Directory.CreateDirectory(siteLogDir);
                var siteLogPath = Path.Combine(siteLogDir, "log.md");
                await File.AppendAllTextAsync(siteLogPath, header, Encoding.UTF8);
            }
        }

        public static async Task AppendEntryAsync(string vaultPath, string siteName, AuditLogEntry entry)
        {
            var sb = new StringBuilder();

            if (entry.Action == "SKIPPED")
            {
                sb.AppendLine($"- **SKIPPED**: `{entry.SourcePath}`");
                sb.AppendLine($"  - Reason: `{entry.SkipReason ?? "unchanged"}`");
            }
            else
            {
                sb.AppendLine($"- **{entry.Action}**: `{entry.SourcePath}` (hash: {entry.ContentHash})");
                if (entry.CreatedEntities.Count > 0)
                    sb.AppendLine($"  - Created Entities: {string.Join(", ", entry.CreatedEntities.ConvertAll(e => $"`{e}`"))}");
                if (entry.ReusedEntities.Count > 0)
                    sb.AppendLine($"  - Reused Entities: {string.Join(", ", entry.ReusedEntities.ConvertAll(e => $"`{e}`"))}");
                if (entry.AssociatedConcepts.Count > 0)
                    sb.AppendLine($"  - Associated Concepts: {string.Join(", ", entry.AssociatedConcepts.ConvertAll(c => $"`{c}`"))}");
                if (entry.SkippedCandidates.Count > 0)
                    sb.AppendLine($"  - Skipped Noise: {string.Join(", ", entry.SkippedCandidates.ConvertAll(s => $"`{s}`"))}");
            }

            foreach (var w in entry.Warnings)
            {
                sb.AppendLine($"  - **WARNING**: {w}");
            }

            var text = sb.ToString();

            // 1. Global log: wiki/log.md
            var globalLogPath = Path.Combine(vaultPath, "wiki", "log.md");
            await File.AppendAllTextAsync(globalLogPath, text, Encoding.UTF8);

            // 2. Site-specific log: wiki/sources/{site}/log.md
            if (!string.IsNullOrWhiteSpace(siteName))
            {
                var siteLogDir = Path.Combine(vaultPath, "wiki", "sources", siteName);
                Directory.CreateDirectory(siteLogDir);
                var siteLogPath = Path.Combine(siteLogDir, "log.md");
                await File.AppendAllTextAsync(siteLogPath, text, Encoding.UTF8);
            }
        }
    }
}
