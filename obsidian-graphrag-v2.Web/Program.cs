using ObsidianGraphRag.Shared;
using obsidian_graphrag_v2.Shared.Services;
using obsidian_graphrag_v2.Web.Components;
using obsidian_graphrag_v2.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using ObsidianGraphRag.Shared.Services.Ingestion;
using ObsidianGraphRag.Shared.Services.Lint;
using ObsidianGraphRag.Shared.Services.LLM;
using ObsidianGraphRag.Shared.Services.N8n;
using ObsidianGraphRag.Shared.Services.Retrieval;
using ObsidianGraphRag.Shared.Services.SiteManager;
using ObsidianGraphRag.Shared.Services.Vault;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment() &&
    Uri.TryCreate(builder.Configuration["GraphRag:DockerDashboardUrl"], UriKind.Absolute, out var dockerDashboard) &&
    dockerDashboard.IsLoopback &&
    dockerDashboard.Scheme is "http" or "https")
{
    // Launch profiles forward to the Docker portal. Docker owns the sign-in session.
    var launcher = builder.Build();
    var dashboardOrigin = dockerDashboard.GetLeftPart(UriPartial.Authority);
    launcher.Run(context =>
    {
        var request = context.Request;
        var query = request.Path.Equals("/account/sign-in") ? "" : request.QueryString.ToUriComponent();
        context.Response.Redirect(dashboardOrigin + request.PathBase.ToUriComponent() +
            request.Path.ToUriComponent() + query);
        return Task.CompletedTask;
    });
    launcher.Run();
    return;
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add device-specific services used by the obsidian_graphrag_v2.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Add Obsidian GraphRAG Core Services
builder.Services.AddObsidianGraphRag(builder.Configuration, registerLocalWorkspace: false);
var accounts = new PortalAccounts(builder.Configuration);
builder.Services.AddSingleton(accounts);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(accounts.KeysPath));
builder.Services.AddSingleton<CrawlAccess>();
builder.Services.AddSingleton<PortalWorkspaces>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, PortalAuthenticationState>();
builder.Services.AddScoped<CircuitHandler, PortalCircuitAccess>();
builder.Services.AddAuthentication("Portal").AddCookie("Portal", options =>
{
    options.LoginPath = "/account/sign-in";
    options.Cookie.Name = "GraphRag.Portal";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = async context =>
    {
        if (context.Principal is null || accounts.GetWorkspaceId(context.Principal) is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync("Portal");
        }
    };
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else
            context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(
    new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("account", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddScoped(sp =>
{
    var state = sp.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync().GetAwaiter().GetResult();
    var id = accounts.GetWorkspaceId(state.User) ?? throw new UnauthorizedAccessException();
    return sp.GetRequiredService<PortalWorkspaces>().For(id);
});
builder.Services.AddScoped(sp => new VaultGraphEngine(sp.GetRequiredService<PortalWorkspaces.Workspace>().Path));
builder.Services.AddScoped(sp => sp.GetRequiredService<PortalWorkspaces.Workspace>().Ingest);
builder.Services.AddScoped(sp => sp.GetRequiredService<PortalWorkspaces.Workspace>().Crawl);
builder.Services.AddScoped<WikiLintEngine>();
builder.Services.AddScoped<SiteManagerService>();
builder.Services.AddScoped(sp => new GraphRagOrchestrator(sp.GetRequiredService<VaultGraphEngine>(),
    sp.GetRequiredService<ILlmClient>(), sp.GetRequiredService<PortalWorkspaces.Workspace>().Search));
builder.Services.AddHttpClient("PortalDocling", client =>
{
    if (Uri.TryCreate(builder.Configuration["Docling:BaseUrl"], UriKind.Absolute, out var address))
        client.BaseAddress = address;
});
builder.Services.AddScoped(sp => new DocumentImportService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("PortalDocling"), sp.GetRequiredService<VaultGraphEngine>()));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseHttpsRedirection();

app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true && context.GetEndpoint()?.Metadata
            .GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is null)
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
PortalAccountEndpoints.Map(app);

if (builder.Configuration.GetValue<int?>("CrawlStorage:Port") is int crawlStoragePort)
{
    CrawlStorageEndpoints.Map(app, crawlStoragePort);
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(
        typeof(ObsidianGraphRag.Shared._Imports).Assembly)
    .RequireAuthorization();

app.MapGet("/api/graph/{site?}", (string? site, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var id = accounts.GetWorkspaceId(context.User);
    return id is null ? Results.Unauthorized() :
        Results.Ok(new VaultGraphEngine(accounts.WorkspacePath(id)).ExportGraphData(site ?? ""));
}).RequireAuthorization();

app.Run();
