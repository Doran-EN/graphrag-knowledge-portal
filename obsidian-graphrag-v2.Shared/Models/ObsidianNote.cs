using System;
using System.Collections.Generic;

namespace ObsidianGraphRag.Shared.Models
{
    public class ObsidianNote
    {
        public string FilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty; // e.g. "wiki/entities/系统_BeautyPOS"
        public string NoteName { get; set; } = string.Empty;     // e.g. "系统_BeautyPOS"
        public string Title { get; set; } = string.Empty;
        public string NoteType { get; set; } = "unknown";        // "raw", "source", "entity", "concept", "comparison", "overview"
        public string Content { get; set; } = string.Empty;
        public string BodyContent { get; set; } = string.Empty;
        public Dictionary<string, object> Frontmatter { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        
        public List<string> Tags { get; set; } = new();
        public List<string> Aliases { get; set; } = new();
        public List<string> Sources { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Status { get; set; } = "synced";
        public DateTime? LastUpdated { get; set; }

        public List<string> OutgoingLinks { get; set; } = new();
        public List<string> IncomingLinks { get; set; } = new();
        public float[]? Embedding { get; set; }
    }
}
