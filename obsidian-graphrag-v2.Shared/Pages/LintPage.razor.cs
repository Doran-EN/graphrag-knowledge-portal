using Microsoft.AspNetCore.Components;
using ObsidianGraphRag.Shared.Models;
using ObsidianGraphRag.Shared.Services.Lint;

namespace ObsidianGraphRag.Shared.Pages;

public partial class LintPage
{
    [Inject]
    private WikiLintEngine LintEngine { get; set; } = default!;

    private LintReport report = new();

    protected override async Task OnInitializedAsync()
    {
        await RunHealthCheck();
    }

    private async Task RunHealthCheck()
    {
        report = await LintEngine.RunLintAsync(false);
    }

    private async Task FixIssue(LintIssue issue)
    {
        await LintEngine.ApplyAutoFixAsync(issue);
        StateHasChanged();
    }
}
