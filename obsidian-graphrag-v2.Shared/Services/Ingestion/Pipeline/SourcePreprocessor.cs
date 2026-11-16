using System;
using System.Collections.Generic;
using System.IO;

namespace ObsidianGraphRag.Shared.Services.Ingestion.Pipeline
{
    public enum ContentClass
    {
        Functional,
        Navigation,
        Policy,
        Knowledge
    }

    public class SourcePreprocessResult
    {
        public ContentClass ContentClass { get; set; } = ContentClass.Knowledge;
        public bool Extractable { get; set; } = true;
        public string? SkipReason { get; set; }
    }

    public static class SourcePreprocessor
    {
        private static readonly HashSet<string> FunctionalKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "cart", "checkout", "login", "signup", "register", "search", "account",
            "thank_you", "contact_us", "cookie", "404", "forgot_password"
        };

        private static readonly HashSet<string> NavigationKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "sitemap", "all_products", "collections", "category"
        };

        private static readonly HashSet<string> PolicyKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "privacy", "privacy_policy", "terms", "terms_of_use", "disclaimer", "compliance"
        };

        public static SourcePreprocessResult Evaluate(string rawFilePath, bool extractPolicy = false)
        {
            var fileName = Path.GetFileNameWithoutExtension(rawFilePath).ToLowerInvariant();

            foreach (var kw in FunctionalKeywords)
            {
                if (fileName == kw || fileName.Contains($"-{kw}-") || fileName.Contains($"_{kw}_") ||
                    fileName.EndsWith($"-{kw}") || fileName.EndsWith($"_{kw}") ||
                    fileName.StartsWith($"{kw}-") || fileName.StartsWith($"{kw}_"))
                {
                    return new SourcePreprocessResult
                    {
                        ContentClass = ContentClass.Functional,
                        Extractable = false,
                        SkipReason = $"functional_page ({kw})"
                    };
                }
            }

            foreach (var kw in NavigationKeywords)
            {
                if (fileName == kw || fileName.Contains($"-{kw}-") || fileName.Contains($"_{kw}_") ||
                    fileName.EndsWith($"-{kw}") || fileName.EndsWith($"_{kw}") ||
                    fileName.StartsWith($"{kw}-") || fileName.StartsWith($"{kw}_"))
                {
                    return new SourcePreprocessResult
                    {
                        ContentClass = ContentClass.Navigation,
                        Extractable = false,
                        SkipReason = $"navigation_index ({kw})"
                    };
                }
            }

            foreach (var kw in PolicyKeywords)
            {
                if (fileName == kw || fileName.Contains($"-{kw}-") || fileName.Contains($"_{kw}_") ||
                    fileName.EndsWith($"-{kw}") || fileName.EndsWith($"_{kw}") ||
                    fileName.StartsWith($"{kw}-") || fileName.StartsWith($"{kw}_"))
                {
                    return new SourcePreprocessResult
                    {
                        ContentClass = ContentClass.Policy,
                        Extractable = extractPolicy,
                        SkipReason = extractPolicy ? null : $"policy_page ({kw})"
                    };
                }
            }

            return new SourcePreprocessResult
            {
                ContentClass = ContentClass.Knowledge,
                Extractable = true
            };
        }
    }
}
