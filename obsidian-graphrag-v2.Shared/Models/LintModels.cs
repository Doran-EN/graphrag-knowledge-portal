using System;
using System.Collections.Generic;

namespace ObsidianGraphRag.Shared.Models
{
    public enum LintIssueType
    {
        UnlinkedMention,
        OrphanPage,
        BrokenLink,
        DuplicateEntity,
        Contradiction,
        DataGap,
        SuspiciousAliasExplosion
    }

    public class LintIssue
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public LintIssueType Type { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string TargetNotePath { get; set; } = string.Empty;
        public string RelatedEntityOrConcept { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
        public bool CanAutoFix { get; set; } = true;
        public string AutoFixActionName { get; set; } = "Auto-Fix";
        public string AutoFixPayload { get; set; } = string.Empty;
        public bool IsFixed { get; set; } = false;
    }

    public class LintReport
    {
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        public int HealthScore { get; set; } = 100;
        public int TotalNotesChecked { get; set; }
        public int TotalIssuesFound => Issues.Count;
        public int UnlinkedMentionsCount { get; set; }
        public int OrphanPagesCount { get; set; }
        public int BrokenLinksCount { get; set; }
        public int DuplicateEntitiesCount { get; set; }
        public int ContradictionsCount { get; set; }
        public List<LintIssue> Issues { get; set; } = new();
    }
}
