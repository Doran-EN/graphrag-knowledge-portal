using System;
using System.Collections.Generic;

namespace ObsidianGraphRag.Shared.Models
{
    public enum MediaResourceType
    {
        Image,
        Video
    }

    public enum MediaResourceRole
    {
        Content,
        Diagram,
        Screenshot,
        Decorative,
        Unknown
    }

    public class MediaResource
    {
        public MediaResourceType Type { get; set; }
        public string Url { get; set; } = string.Empty;
        public string? Alt { get; set; }
        public string? Title { get; set; }
        public MediaResourceRole Role { get; set; } = MediaResourceRole.Content;
        public string? Provider { get; set; } // "YouTube", "Vimeo", "Direct", etc.
    }

    public class MediaExtractionResult
    {
        public List<MediaResource> Media { get; set; } = new();
        public string SanitizedContentForLlm { get; set; } = string.Empty;
    }
}
