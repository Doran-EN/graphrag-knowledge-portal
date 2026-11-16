using System.Text.RegularExpressions;
using Markdig;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;
using ObsidianGraphRag.Shared.Models;

namespace ObsidianGraphRag.Shared.Pages;

internal static class ChatAnswerMarkdown
{
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();
    private static readonly Regex CitationPattern = new(@"\[(\d+(?:\s*,\s*\d+)*)\](?!\()", RegexOptions.CultureInvariant);

    internal static MarkupString Render(string text)
    {
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<LinkInlineRenderer>(new SafeLinkRenderer());
        renderer.Render(Markdown.Parse(text, Pipeline));
        return new MarkupString(writer.ToString());
    }

    internal static List<(int Number, CitationInfo Source)> GetCitedSources(string answer, List<CitationInfo> candidates)
    {
        var numbers = CitationPattern.Matches(answer)
            .SelectMany(match => match.Groups[1].Value.Split(','))
            .Select(value => int.TryParse(value, out var number) ? number : 0)
            .Where(number => number > 0 && number <= candidates.Count)
            .Distinct();
        return numbers.Select(number => (number, candidates[number - 1])).ToList();
    }

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().DisableHtml().UsePipeTables();
        builder.InlineParsers.TryRemove<AutolinkInlineParser>();
        return builder.Build();
    }

    private sealed class SafeLinkRenderer : LinkInlineRenderer
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            var destination = link.GetDynamicUrl?.Invoke() ?? link.Url;
            if (!link.IsImage && Uri.TryCreate(destination, UriKind.Absolute, out var url) &&
                url.Scheme is "http" or "https")
            {
                base.Write(renderer, link);
                return;
            }

            renderer.WriteChildren(link);
        }
    }
}
