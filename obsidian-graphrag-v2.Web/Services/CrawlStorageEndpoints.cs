using System.Text.RegularExpressions;

namespace obsidian_graphrag_v2.Web.Services;

internal static class CrawlStorageEndpoints
{
    public static void Map(WebApplication app, int port)
    {
        app.MapGet("/internal/crawl/session", (HttpContext context, CrawlAccess access, PortalWorkspaces workspaces) =>
        {
            if (Authorize(context, access, port) is not { } grant)
                return Results.NotFound();

            var crawl = workspaces.For(grant.WorkspaceId).Crawl;
            if (!int.TryParse(context.Request.Query["version"], out var version))
                version = 0;
            if (!crawl.VerifyWorkflow(grant.Token, grant.Site, version))
                return Results.NotFound();
            return Results.Json(new { site = grant.Site, url = grant.Url });
        }).AllowAnonymous();
        app.MapMethods("/internal/crawl/raw/{site}/{fileName}", ["GET", "PUT", "DELETE"],
            (HttpContext context, string site, string fileName, CrawlAccess access, PortalWorkspaces workspaces) =>
                HandleAsync(context, site, fileName, Authorize(context, access, port), workspaces)).AllowAnonymous();
    }

    private static CrawlAccess.CrawlGrant? Authorize(HttpContext context, CrawlAccess access, int port) =>
        context.Connection.LocalPort == port && context.Request.Headers.TryGetValue("X-GraphRag-Crawl", out var token)
            ? access.Read(token.ToString())
            : null;

    private static async Task<IResult> HandleAsync(
        HttpContext context, string site, string fileName, CrawlAccess.CrawlGrant? grant, PortalWorkspaces workspaces)
    {
        if (grant is not { } workspace || !string.Equals(workspace.Site, site, StringComparison.Ordinal))
            return Results.NotFound();

        var crawl = workspaces.For(workspace.WorkspaceId).Crawl;
        if (!crawl.CanAcceptCallback(workspace.Token))
            return Results.NotFound();

        if (fileName == "_crawl_progress.json" && HttpMethods.IsPut(context.Request.Method))
        {
            var progress = await context.Request.ReadFromJsonAsync<CrawlProgressUpdate>(context.RequestAborted);
            if (progress is null)
                return Results.BadRequest();

            if (progress.Total is { } total)
                return CallbackResult(crawl.RecordQueue(workspace.Token, total));
            if (progress.Page is { } page)
                return CallbackResult(crawl.RecordPageChecked(workspace.Token, page, progress.Failed));
            if (progress.Discovering)
                return CallbackResult(crawl.RecordDiscovery(workspace.Token));
            return Results.BadRequest();
        }

        var isMarkdown = Regex.IsMatch(fileName, @"\A[A-Za-z0-9_-]{1,80}\.md\z");
        var isIndex = fileName == "_crawl_index.json";
        var isReport = fileName == "_crawl_report.json";
        if (!Regex.IsMatch(site, @"\A[A-Za-z0-9_-]{1,63}\z") ||
            !(isMarkdown || isIndex || isReport))
        {
            return Results.BadRequest();
        }

        var rawPath = Path.Combine(workspace.Path, "raw");
        var sitePath = Path.Combine(rawPath, site);
        var filePath = Path.Combine(sitePath, fileName);
        var method = context.Request.Method;

        if (HttpMethods.IsGet(method) && isIndex)
        {
            return File.Exists(filePath)
                ? Results.File(filePath, "application/json")
                : Results.NotFound();
        }

        if (HttpMethods.IsDelete(method) && isMarkdown)
        {
            return CallbackResult(crawl.RecordPageDeleted(workspace.Token, fileName, () =>
            {
                if (Directory.Exists(sitePath))
                    File.Delete(filePath);
            }));
        }

        if (!HttpMethods.IsPut(method))
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        Directory.CreateDirectory(rawPath);
        var temporaryPath = Path.Combine(rawPath, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var temporaryFile = File.Create(temporaryPath))
            {
                await context.Request.Body.CopyToAsync(temporaryFile, context.RequestAborted);
            }
            void Commit()
            {
                Directory.CreateDirectory(sitePath);
                File.Move(temporaryPath, filePath, overwrite: true);
            }
            if (isReport)
                return CallbackResult(crawl.RecordReport(workspace.Token, site, filePath, Commit));
            if (isIndex)
                return CallbackResult(crawl.RecordIndexSaved(workspace.Token, Commit));
            return CallbackResult(crawl.RecordPageSaved(workspace.Token, fileName, Commit));
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static IResult CallbackResult(bool accepted) => accepted ? Results.Json(new { ok = true }) : Results.NotFound();

    private sealed record CrawlProgressUpdate(int? Total, int? Page, bool Failed, bool Discovering);
}
