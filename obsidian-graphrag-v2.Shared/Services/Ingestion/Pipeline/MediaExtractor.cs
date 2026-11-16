using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ObsidianGraphRag.Shared.Models;

namespace ObsidianGraphRag.Shared.Services.Ingestion.Pipeline
{
    public static class MediaExtractor
    {
        // 1. YouTube Video detection regexes
        private static readonly Regex YouTubeUrlRegex = new(
            @"(?:https?:\/\/)?(?:www\.)?(?:youtube\.com\/(?:watch\?v=|embed\/|shorts\/)|youtu\.be\/)([\w\-]{11})(?:[^\s\)\""'<]*)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MarkdownLinkRegex = new(
            @"\[([^\]]+)\]\((https?:\/\/[^\)]+)\)",
            RegexOptions.Compiled);

        // 2. Markdown Image detection regex
        private static readonly Regex MarkdownImageRegex = new(
            @"!\[([^\]]*)\]\(([^\)]+)\)",
            RegexOptions.Compiled);

        // 3. Obvious decorative noise keywords in URL / Filename
        private static readonly HashSet<string> DecorativeKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "logo", "icon", "favicon", "avatar", "spacer", "spinner", "pixel",
            "social", "facebook", "twitter", "instagram", "linkedin", "whatsapp", "wechat",
            "star-rating", "rating", "bullet", "arrow", "badge"
        };

        public static MediaExtractionResult Extract(string rawMarkdown)
        {
            if (string.IsNullOrWhiteSpace(rawMarkdown))
            {
                return new MediaExtractionResult
                {
                    Media = new List<MediaResource>(),
                    SanitizedContentForLlm = string.Empty
                };
            }

            var mediaList = new List<MediaResource>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Step 1: Extract and Replace Markdown Images
            var contentWithImagesProcessed = MarkdownImageRegex.Replace(rawMarkdown, match =>
            {
                var alt = match.Groups[1].Value.Trim();
                var rawUrl = match.Groups[2].Value.Trim();

                // Clean URL of optional title attribute in markdown: ![alt](url "title")
                var cleanUrl = rawUrl.Split(' ', 2)[0].Trim().Trim('<', '>');
                if (string.IsNullOrWhiteSpace(cleanUrl)) return string.Empty;

                var role = DetermineImageRole(alt, cleanUrl);

                if (!seenUrls.Contains(cleanUrl) && role != MediaResourceRole.Decorative)
                {
                    seenUrls.Add(cleanUrl);
                    mediaList.Add(new MediaResource
                    {
                        Type = MediaResourceType.Image,
                        Url = cleanUrl,
                        Alt = string.IsNullOrWhiteSpace(alt) ? null : alt,
                        Title = string.IsNullOrWhiteSpace(alt) ? null : alt,
                        Role = role,
                        Provider = "Direct"
                    });
                }

                // Return concise semantic placeholder for LLM
                return string.IsNullOrWhiteSpace(alt) ? "[Image resource]" : $"[Image: {alt}]";
            });

            // Step 2: Extract and Replace Markdown Links that point to YouTube
            var contentWithLinksProcessed = MarkdownLinkRegex.Replace(contentWithImagesProcessed, match =>
            {
                var linkText = match.Groups[1].Value.Trim();
                var url = match.Groups[2].Value.Trim();

                var ytMatch = YouTubeUrlRegex.Match(url);
                if (ytMatch.Success)
                {
                    var videoId = ytMatch.Groups[1].Value;
                    var canonicalUrl = $"https://www.youtube.com/watch?v={videoId}";

                    if (!seenUrls.Contains(canonicalUrl))
                    {
                        seenUrls.Add(canonicalUrl);
                        mediaList.Add(new MediaResource
                        {
                            Type = MediaResourceType.Video,
                            Url = canonicalUrl,
                            Title = linkText,
                            Alt = linkText,
                            Role = MediaResourceRole.Content,
                            Provider = "YouTube"
                        });
                    }

                    return $"[Video: {linkText}]";
                }

                // Leave regular non-YouTube markdown links intact
                return match.Value;
            });

            // Step 3: Extract and Replace Bare YouTube URLs (not wrapped in markdown links)
            var contentWithBareVideosProcessed = YouTubeUrlRegex.Replace(contentWithLinksProcessed, match =>
            {
                var fullMatchUrl = match.Value;
                var videoId = match.Groups[1].Value;
                var canonicalUrl = $"https://www.youtube.com/watch?v={videoId}";

                if (!seenUrls.Contains(canonicalUrl))
                {
                    seenUrls.Add(canonicalUrl);
                    mediaList.Add(new MediaResource
                    {
                        Type = MediaResourceType.Video,
                        Url = canonicalUrl,
                        Title = "YouTube Video",
                        Alt = "YouTube Video",
                        Role = MediaResourceRole.Content,
                        Provider = "YouTube"
                    });
                }

                return "[Video: YouTube Tutorial/Resource]";
            });

            // Clean up excessive blank lines created during media stripping
            var finalSanitized = Regex.Replace(contentWithBareVideosProcessed, @"\n{3,}", "\n\n").Trim();

            return new MediaExtractionResult
            {
                Media = mediaList,
                SanitizedContentForLlm = finalSanitized
            };
        }

        private static MediaResourceRole DetermineImageRole(string alt, string url)
        {
            var lowerUrl = url.ToLowerInvariant();
            var lowerAlt = alt.ToLowerInvariant();

            // 1. Check decorative / logo / icon keywords
            foreach (var kw in DecorativeKeywords)
            {
                if (lowerUrl.Contains(kw) || lowerAlt.Contains(kw))
                {
                    return MediaResourceRole.Decorative;
                }
            }

            // 2. Check diagram / workflow / architecture
            if (lowerAlt.Contains("diagram") || lowerAlt.Contains("architecture") || lowerAlt.Contains("workflow") ||
                lowerAlt.Contains("schema") || lowerAlt.Contains("flow") || lowerAlt.Contains("流程") || lowerAlt.Contains("架构"))
            {
                return MediaResourceRole.Diagram;
            }

            // 3. Check screenshot / UI / sample
            if (lowerAlt.Contains("screenshot") || lowerAlt.Contains("ui") || lowerAlt.Contains("screen") ||
                lowerAlt.Contains("interface") || lowerAlt.Contains("截图") || lowerAlt.Contains("界面"))
            {
                return MediaResourceRole.Screenshot;
            }

            // 4. Default content image
            return MediaResourceRole.Content;
        }
    }
}
