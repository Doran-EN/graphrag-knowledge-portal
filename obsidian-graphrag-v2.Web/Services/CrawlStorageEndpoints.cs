using System.Text.RegularExpressions;

namespace obsidian_graphrag_v2.Web.Services;

internal static class CrawlStorageEndpoints
{
    public static void Map(WebApplication app, int port)
    {
        app.MapGet("/internal/crawl/session", (HttpContext context, CrawlAccess access) =>
            Authorize(context, access, port) is { } grant
                ? Results.Json(new { site = grant.Site, url = grant.Url })
                : Results.NotFound()).AllowAnonymous();
        app.MapMethods("/internal/crawl/raw/{site}/{fileName}", ["GET", "PUT", "DELETE"],
            (HttpContext context, string site, string fileName, CrawlAccess access) =>
                HandleAsync(context, site, fileName, Authorize(context, access, port))).AllowAnonymous();
    }

    private static (string Path, string Site, string Url)? Authorize(HttpContext context, CrawlAccess access, int port) =>
        context.Connection.LocalPort == port && context.Request.Headers.TryGetValue("X-GraphRag-Crawl", out var token)
            ? access.Read(token.ToString())
            : null;

    private static async Task<IResult> HandleAsync(
        HttpContext context, string site, string fileName, (string Path, string Site, string Url)? grant)
    {
        if (grant is not { } workspace || !string.Equals(workspace.Site, site, StringComparison.Ordinal))
            return Results.NotFound();

        var isMarkdown = Regex.IsMatch(fileName, @"\A[A-Za-z0-9_-]{1,80}\.md\z");
        var isIndex = fileName == "_crawl_index.json";
        var isReport = fileName == "_crawl_report.json";
        if (!Regex.IsMatch(site, @"\A[A-Za-z0-9_-]{1,63}\z") ||
            !(isMarkdown || isIndex || isReport))
        {
            return Results.BadRequest();
        }

        var sitePath = Path.Combine(workspace.Path, "raw", site);
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
            if (Directory.Exists(sitePath))
                File.Delete(filePath);
            return Results.Json(new { ok = true });
        }

        if (!HttpMethods.IsPut(method))
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        Directory.CreateDirectory(sitePath);
        var temporaryPath = Path.Combine(sitePath, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var temporaryFile = File.Create(temporaryPath))
            {
                await context.Request.Body.CopyToAsync(temporaryFile, context.RequestAborted);
            }
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return Results.Json(new { ok = true });
    }
}
