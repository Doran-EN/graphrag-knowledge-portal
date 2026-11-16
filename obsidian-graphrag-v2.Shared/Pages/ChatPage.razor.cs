using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using ObsidianGraphRag.Shared.Models;
using ObsidianGraphRag.Shared.Services.Ingestion;
using ObsidianGraphRag.Shared.Services.Retrieval;
using ObsidianGraphRag.Shared.Services.SiteManager;

namespace ObsidianGraphRag.Shared.Pages;

public partial class ChatPage
{
    [Inject]
    private GraphRagOrchestrator Orchestrator { get; set; } = default!;

    [Inject]
    private SiteManagerService SiteManager { get; set; } = default!;

    [Inject]
    private DocumentImportService DocumentImport { get; set; } = default!;

    [Inject]
    private ILogger<ChatPage> Logger { get; set; } = default!;

    public class ChatDisplayMessage
    {
        public bool IsUser { get; set; }
        public string Text { get; set; } = string.Empty;
        public string ContextLabel { get; set; } = string.Empty;
        public MarkupString? FormattedAnswer { get; set; }
        public List<(int Number, CitationInfo Source)>? Citations { get; set; }
    }

    private readonly List<string> detectedSites = new();
    private readonly Dictionary<string, (DocumentImportService.UploadedDocument Document, string Label)> documentOptions = new();
    private ChatSourceType sourceType = ChatSourceType.Both;
    private string selectedSource = "";

    private bool HasSources => sourceType switch
    {
        ChatSourceType.Websites => detectedSites.Count > 0,
        ChatSourceType.Documents => documentOptions.Count > 0,
        _ => true
    };

    private string CurrentContextLabel => sourceType switch
    {
        ChatSourceType.Websites => selectedSource.Length == 0 ? "All websites" : selectedSource,
        ChatSourceType.Documents => documentOptions.TryGetValue(selectedSource, out var option)
            ? option.Label : "All uploaded documents",
        _ => ""
    };

    private List<ChatDisplayMessage> messages = new()
    {
        new()
        {
            IsUser = false,
            Text = "Ask about the websites and documents in your knowledge base."
        }
    };

    private string inputQuery = "";
    private ChatQueryStage? queryStage;

    private string QueryStatus => queryStage switch
    {
        ChatQueryStage.ReadingQuestion => "Reading your question…",
        ChatQueryStage.SearchingSources => "Searching sources…",
        ChatQueryStage.WritingAnswer => "Writing your answer…",
        _ => ""
    };

    protected override void OnInitialized()
    {
        var sites = SiteManager.GetSiteNames();
        detectedSites.AddRange(sites.Where(site => !site.Equals(DocumentImportService.Collection, StringComparison.OrdinalIgnoreCase))
            .OrderBy(site => site, StringComparer.OrdinalIgnoreCase));
        foreach (var group in DocumentImport.GetUploadedDocuments().GroupBy(document => document.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var uploads = group.ToArray();
            for (var index = 0; index < uploads.Length; index++)
            {
                var document = uploads[index];
                var label = uploads.Length == 1 ? document.FileName : $"{document.FileName} (upload {index + 1})";
                documentOptions.Add(document.Id, (document, label));
            }
        }
    }

    private void ChangeSourceType(ChatSourceType type)
    {
        sourceType = type;
        selectedSource = "";
    }

    private void OnSourceChanged(ChangeEventArgs e)
    {
        var source = e.Value?.ToString() ?? "";
        if (source.Length == 0 ||
            (sourceType == ChatSourceType.Websites && detectedSites.Contains(source)) ||
            (sourceType == ChatSourceType.Documents && documentOptions.ContainsKey(source)))
            selectedSource = source;
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(inputQuery))
        {
            await SendMessage();
        }
    }

    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(inputQuery) || queryStage is not null || !HasSources) return;

        var q = inputQuery.Trim();
        inputQuery = "";
        messages.Add(new ChatDisplayMessage { IsUser = true, Text = q });
        queryStage = ChatQueryStage.ReadingQuestion;
        StateHasChanged();

        var label = CurrentContextLabel;
        var site = sourceType == ChatSourceType.Websites ? selectedSource : "";
        var document = sourceType == ChatSourceType.Documents && documentOptions.TryGetValue(selectedSource, out var option)
            ? option.Document : null;
        try
        {
            var result = await Orchestrator.QueryAsync(q, sourceType, site, document, ShowQueryStageAsync);
            messages.Add(new ChatDisplayMessage
            {
                Text = result.Answer,
                FormattedAnswer = ChatAnswerMarkdown.Render(result.Answer),
                ContextLabel = label,
                Citations = ChatAnswerMarkdown.GetCitedSources(result.Answer, result.Citations)
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Chat could not prepare an answer.");
            messages.Add(new ChatDisplayMessage
            {
                Text = "Something went wrong while preparing your answer. Please try again.",
                ContextLabel = label
            });
        }
        finally
        {
            queryStage = null;
        }
    }

    private Task ShowQueryStageAsync(ChatQueryStage stage) => InvokeAsync(() =>
    {
        queryStage = stage;
        StateHasChanged();
    });

    private void NewChat()
    {
        messages.Clear();
        messages.Add(new ChatDisplayMessage
        {
            IsUser = false,
            Text = sourceType == ChatSourceType.Both ? "Ask about the websites and documents in your knowledge base." : $"Ask about {CurrentContextLabel}."
        });
    }
}
