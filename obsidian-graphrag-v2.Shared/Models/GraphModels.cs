using System;
using System.Collections.Generic;

namespace ObsidianGraphRag.Shared.Models
{
    public class GraphEdge
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string RelationType { get; set; } = "related_to"; // belongs_to, has_feature, mentions, subconcept_of
        public string ContextSnippet { get; set; } = string.Empty;
    }

    public class GraphNodeDto
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Type { get; set; } = "entity"; // entity, concept, source, raw
        public string Summary { get; set; } = string.Empty;
        public int Degree { get; set; }
        public string Group { get; set; } = string.Empty; // Website or group name
        public string Color { get; set; } = string.Empty; // Assigned hex color
    }

    public class GraphEdgeDto
    {
        public string Id { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    public class GraphGroupDto
    {
        public string Name { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public int NodeCount { get; set; }
    }

    public class GraphDataDto
    {
        public List<GraphNodeDto> Nodes { get; set; } = new();
        public List<GraphEdgeDto> Edges { get; set; } = new();
        public List<GraphGroupDto> Groups { get; set; } = new();
    }

    public class SiteKnowledgeSummary
    {
        public string SiteName { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public int RawDocCount { get; set; }
        public int WikiSourceCount { get; set; }
        public int EntityCount { get; set; }
        public int ConceptCount { get; set; }
        public int TotalEdges { get; set; }
        public int HealthScore { get; set; } = 95;
        public DateTime? LastCrawlTime { get; set; }
        public DateTime? LastIngestTime { get; set; }
        public string SyncStatus { get; set; } = "Synced"; // Synced, PendingUpdates, Ingesting
    }

    public class SiteFileTreeNode
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = "file"; // "folder" or "file"
        public string NoteType { get; set; } = string.Empty; // "raw", "source", "entity", "concept"
        public List<SiteFileTreeNode> Children { get; set; } = new();
        public bool IsExpanded { get; set; } = true;
    }

    public class GraphSearchResult
    {
        public string Query { get; set; } = string.Empty;
        public string Answer { get; set; } = string.Empty;
        public List<string> ExtractedEntities { get; set; } = new();
        public List<ObsidianNote> RetrievedNotes { get; set; } = new();
        public List<string> ReasoningPath { get; set; } = new();
        public List<CitationInfo> Citations { get; set; } = new();
    }

    public class CitationInfo
    {
        public string Title { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
        public List<MediaResource> MediaResources { get; set; } = new();
    }
}
