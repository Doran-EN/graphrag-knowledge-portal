using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace obsidian_graphrag_v2.Web.Services;

internal static class PortalAccountEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/account/sign-in", (HttpContext context, IAntiforgery csrf, PortalAccounts accounts) =>
            context.User.Identity?.IsAuthenticated == true ? Results.Redirect("/") :
                Page(context, csrf, accounts.NeedsOwner ? "/account/setup" : "/account/sign-in"))
            .AllowAnonymous();

        app.MapGet("/account/join", (HttpContext context, IAntiforgery csrf) =>
            Page(context, csrf, "/account/join")).AllowAnonymous();

        app.MapPost("/account/{action}", async (string action, HttpContext context, IAntiforgery csrf, PortalAccounts accounts) =>
        {
            if (!await csrf.IsRequestValidAsync(context))
                return Results.BadRequest("Refresh the page and try again.");

            if (action == "sign-out" && accounts.GetWorkspaceId(context.User) is not null)
            {
                accounts.SignOut(context.User);
                await context.SignOutAsync("Portal");
                return Results.Redirect("/account/sign-in");
            }

            if (action == "invite" && context.User.IsInRole("Owner") &&
                accounts.GetWorkspaceId(context.User) is not null)
                return Page(context, csrf, "/account/manage", invite: accounts.CreateInvitation());

            if (action is not ("sign-in" or "setup" or "join"))
                return Results.NotFound();

            if (context.User.Identity?.IsAuthenticated == true)
                return Results.Redirect("/");

            var form = await context.Request.ReadFormAsync();
            var username = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            var target = "/account/" + action;
            if (action != "sign-in" && !accounts.Register(username, password, form["code"].ToString().Trim(), action == "setup"))
                return Page(context, csrf, target, "Could not create the account. Check the code and use an available email address.");

            var principal = accounts.SignIn(username, password);
            if (principal is null)
                return Page(context, csrf, target, "The email or password is incorrect.");
            accounts.SignOut(context.User);
            await context.SignInAsync("Portal", principal);
            return Results.Redirect("/");
        }).AllowAnonymous().RequireRateLimiting("account");

        app.MapGet("/account/manage", (HttpContext context, IAntiforgery csrf) =>
            Page(context, csrf, "/account/manage")).RequireAuthorization();
    }

    private static IResult Page(HttpContext context, IAntiforgery csrf, string action, string? error = null, string? invite = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var tokens = csrf.GetAndStoreTokens(context);
        var antiforgery = $"<input type=\"hidden\" name=\"{HtmlEncoder.Default.Encode(tokens.FormFieldName)}\" value=\"{HtmlEncoder.Default.Encode(tokens.RequestToken!)}\">";
        var manage = action == "/account/manage";
        var signIn = action == "/account/sign-in";
        var setup = action == "/account/setup";
        var title = manage ? "Your account" : signIn ? "Sign in" : setup ? "Create the owner account" : "Join the portal";
        var errorMarkup = error is null ? "" : $"<p class=\"account-error\" role=\"alert\">{HtmlEncoder.Default.Encode(error)}</p>";
        var codeField = signIn ? "" : $"""
            <label for="code">{(setup ? "Owner setup code" : "Invitation code")}</label>
            <input id="code" name="code" maxlength="64" required autocomplete="off" spellcheck="false">
            """;
        var fields = $"""
            <label for="email">Email</label>
            <input id="email" name="email" type="{(signIn ? "text" : "email")}" inputmode="email" required maxlength="254" autocomplete="username" spellcheck="false">
            <label for="password">Password{(signIn || setup ? "" : " (at least 12 characters)")}</label>
            <input id="password" name="password" type="password" required minlength="{(signIn || setup ? 4 : 12)}" maxlength="128" autocomplete="{(signIn ? "current-password" : "new-password")}">
            {codeField}
            """;
        var inviteMarkup = invite is null ? "" : $"""
            <p>Share this single-use code with the person you want to invite. It expires in 24 hours.</p>
            <output class="invite-code">{HtmlEncoder.Default.Encode(invite)}</output>
            """;
        var inviteForm = context.User.IsInRole("Owner") ? $"""
            <form method="post" action="/account/invite">{antiforgery}
                <button type="submit">Create invitation</button>
            </form>
            """ : "";
        var body = manage ? $"""
            <p>Signed in as <strong>{HtmlEncoder.Default.Encode(context.User.Identity?.Name ?? "")}</strong></p>
            {inviteMarkup}
            {inviteForm}
            <form method="post" action="/account/sign-out">{antiforgery}
                <button type="submit" class="secondary">Sign out</button>
            </form>
            <a href="/">Back to dashboard</a>
            """ : $"""
            {(setup ? "<p>The owner account will receive the existing knowledge base. Get the setup code from the Docker host.</p>" : "<p>Access your private knowledge workspace.</p>")}
            {errorMarkup}
            <form method="post" action="{action}">{antiforgery}{fields}
                <button type="submit">{title}</button>
            </form>
            {(setup ? "" : signIn ? "<a href=\"/account/join\">Have an invitation?</a>" : "<a href=\"/account/sign-in\">Already have an account?</a>")}
            """;
        return Results.Content($"""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{title} | GraphRAG Portal</title>
            <link rel="stylesheet" href="/_content/obsidian-graphrag-v2.Shared/app.css">
            <link rel="stylesheet" href="/account.css"></head>
            <body class="account-page"><main class="account-card">
            <a class="account-brand" href="/">GraphRAG Portal</a><h1>{title}</h1>{body}
            </main></body></html>
            """, "text/html; charset=utf-8");
    }
}
