using System;
using System.Collections.Concurrent;

namespace ObsidianGraphRag.Shared.Models
{
    public class IngestJobStatus
    {
        public bool IsRunning { get; set; }
        public string TargetName { get; init; } = string.Empty;
        public bool Reprocess { get; init; }
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public int UnchangedFiles { get; set; }
        public int UpdatedFiles { get; set; }
        public string? ActiveSection { get; set; }
        public string? FailedSection { get; set; }
        public ConcurrentQueue<string> SectionsToRetry { get; } = new();
        public int NewEntitiesCreated { get; set; }
        public int NewConceptsCreated { get; set; }
        public string CurrentStepDescription { get; set; } = string.Empty;
        public int ProgressPercent => TotalFiles > 0 ? (int)((ProcessedFiles / (double)TotalFiles) * 100) : 0;
        public ConcurrentQueue<IngestLogEntry> Logs { get; } = new();
    }

    public class IngestLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Level { get; set; } = "INFO"; // INFO, SUCCESS, WARN, ERROR
        public string Message { get; set; } = string.Empty;
    }
}
