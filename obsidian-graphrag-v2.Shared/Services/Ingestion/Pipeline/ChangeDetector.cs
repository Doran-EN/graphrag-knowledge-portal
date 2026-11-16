using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ObsidianGraphRag.Shared.Services.Ingestion.Pipeline
{
    public class ChangeDetectResult
    {
        public bool IsChanged { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public string ExistingHash { get; set; } = string.Empty;
    }

    public static class ChangeDetector
    {
        public static string ComputeContentHash(string content)
        {
            if (string.IsNullOrEmpty(content)) return "empty";
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
            // 12-char compact hex representation
            return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
        }

        public static ChangeDetectResult Evaluate(string rawContent, string? existingHash, bool forceFull = false)
        {
            var hash = ComputeContentHash(rawContent);

            if (forceFull || string.IsNullOrWhiteSpace(existingHash))
            {
                return new ChangeDetectResult
                {
                    IsChanged = true,
                    ContentHash = hash,
                    ExistingHash = existingHash ?? string.Empty
                };
            }

            var cleanExisting = existingHash.Replace("sha256:", "").Trim().ToLowerInvariant();
            bool isChanged = !string.Equals(hash, cleanExisting, StringComparison.OrdinalIgnoreCase);

            return new ChangeDetectResult
            {
                IsChanged = isChanged,
                ContentHash = hash,
                ExistingHash = existingHash
            };
        }
    }
}
