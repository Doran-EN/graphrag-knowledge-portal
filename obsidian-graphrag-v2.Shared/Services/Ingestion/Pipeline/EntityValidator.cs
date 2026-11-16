using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ObsidianGraphRag.Shared.Models;

namespace ObsidianGraphRag.Shared.Services.Ingestion.Pipeline
{
    public static class EntityValidator
    {
        private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
        {
            "cart", "checkout", "about", "about us", "contact", "contact us",
            "learn more", "read more", "click here", "terms", "terms of use",
            "terms and conditions", "privacy", "privacy policy", "search", "menu",
            "all rights reserved", "follow us", "share", "login", "sign in", "sign up",
            "subscribe", "overview", "product", "service", "treatment", "item", "category",
            "description", "home", "shop", "blog", "reviews", "view all", "view more",
            "copyright", "disclaimer", "cookie policy", "newsletter", "unnamed", "unknown",
            "example product title", "blog post", "related skin tips", "recommended products",
            "recommended package", "recommended treatments", "faq", "frequently asked questions"
        };

        public static ValidationResult Validate(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return ValidationResult.Reject("Name is null or whitespace");

            // Strip markdown, quotes, formatting characters
            var cleaned = rawName.Trim().Trim('*', '_', '#', ' ', ':', '-', '"', '\'', '[', ']', '(', ')', '{', '}');

            // 1. Minimum and maximum length check
            if (cleaned.Length < 2)
                return ValidationResult.Reject($"Too short ({cleaned.Length} chars)");
            if (cleaned.Length > 80)
                return ValidationResult.Reject($"Too long ({cleaned.Length} chars)");

            // 2. Pure symbols / punctuation / stars check (e.g. ★★★★★, ***, ---)
            if (Regex.IsMatch(cleaned, @"^[\s\p{P}\p{S}★☆•·—_\-]+$"))
                return ValidationResult.Reject("Consists entirely of symbols or rating stars");

            // 3. Pure digits check
            if (Regex.IsMatch(cleaned, @"^\d+$"))
                return ValidationResult.Reject("Consists entirely of digits");

            // 4. Stopwords check
            if (Stopwords.Contains(cleaned))
                return ValidationResult.Reject($"Matched generic stopword: '{cleaned}'");

            // 5. Check if starts with suspicious phrase
            var lower = cleaned.ToLowerInvariant();
            if (lower.StartsWith("50% off") || lower.StartsWith("book your") || lower.StartsWith("sign up for"))
                return ValidationResult.Reject("Promotional/CTA banner headline");

            // 6. Ensure safe filename
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                cleaned = cleaned.Replace(c, '_');
            }
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(cleaned) || cleaned == "_")
                return ValidationResult.Reject("Invalid filename after sanitization");

            return ValidationResult.Pass(cleaned);
        }
    }
}
