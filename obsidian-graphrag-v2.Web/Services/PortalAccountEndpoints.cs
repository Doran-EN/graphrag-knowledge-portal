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

        app.MapGet("/account/join", (HttpContext context, IAntiforgery csrf, PortalAccounts accounts) =>
            context.User.Identity?.IsAuthenticated == true ? Results.Redirect("/") :
            accounts.NeedsOwner ? Results.Redirect("/account/sign-in") : Page(context, csrf, "/account/join"))
            .AllowAnonymous();

        app.MapGet("/account/request-submitted", (HttpContext context, IAntiforgery csrf) =>
            Page(context, csrf, "/account/request-submitted")).AllowAnonymous();

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

            if (action is not ("sign-in" or "setup" or "join"))
                return Results.NotFound();

            if (context.User.Identity?.IsAuthenticated == true)
                return Results.Redirect("/");

            var form = await context.Request.ReadFormAsync();
            var login = form["email"].ToString().Trim();
            var displayName = form["username"].ToString().Trim();
            var password = form["password"].ToString();
            var target = "/account/" + action;
            if (action != "sign-in" && !PortalAccounts.IsDisplayNameValid(displayName))
                return Page(context, csrf, target, $"Enter a username of up to {PortalAccounts.MaximumDisplayNameLength} characters.");

            if (action == "join")
            {
                if (accounts.NeedsOwner)
                    return Results.Redirect("/account/sign-in");
                return accounts.Register(login, password, displayName, owner: false)
                    ? Results.Redirect("/account/request-submitted")
                    : Page(context, csrf, target, "Check your email address and password. If you already submitted a request, sign in to check its status.");
            }

            if (action == "setup" && !accounts.Register(login, password, displayName, owner: true, setupCode: form["code"].ToString().Trim()))
                return Page(context, csrf, target, "Check your email address, password, and owner setup code.");

            var principal = accounts.SignIn(login, password, out var blockedStatus);
            if (principal is null)
                return Page(context, csrf, target, blockedStatus switch
                {
                    PortalAccounts.AccountStatus.Pending => "Your request is awaiting approval from the portal owner.",
                    PortalAccounts.AccountStatus.Rejected => "The portal owner rejected your account request. Contact the owner for details.",
                    _ => "The email or password is incorrect."
                });
            accounts.SignOut(context.User);
            await context.SignInAsync("Portal", principal);
            return Results.Redirect("/");
        }).AllowAnonymous().RequireRateLimiting("account");

        app.MapGet("/account/manage", (HttpContext context, IAntiforgery csrf) =>
            Page(context, csrf, "/account/manage")).RequireAuthorization();
    }

    private static IResult Page(HttpContext context, IAntiforgery csrf, string action, string? error = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var tokens = csrf.GetAndStoreTokens(context);
        var antiforgery = $"<input type=\"hidden\" name=\"{HtmlEncoder.Default.Encode(tokens.FormFieldName)}\" value=\"{HtmlEncoder.Default.Encode(tokens.RequestToken!)}\">";
        var manage = action == "/account/manage";
        var signIn = action == "/account/sign-in";
        var setup = action == "/account/setup";
        var submitted = action == "/account/request-submitted";
        var title = action switch
        {
            "/account/manage" => "Your account",
            "/account/sign-in" => "Sign in",
            "/account/setup" => "Create the owner account",
            "/account/request-submitted" => "Request submitted",
            _ => "Request an account"
        };
        var errorMarkup = error is null ? "" : $"<p class=\"account-error\" role=\"alert\">{HtmlEncoder.Default.Encode(error)}</p>";
        var codeField = setup ? """
            <label for="code">Owner setup code</label>
            <input id="code" name="code" maxlength="64" required autocomplete="off" spellcheck="false">
            """ : "";
        var usernameField = !setup && action != "/account/join" ? "" : $"""
            <label for="username">Username</label>
            <input id="username" name="username" type="text" required maxlength="{PortalAccounts.MaximumDisplayNameLength}" autocomplete="nickname">
            """;
        var fields = $"""
            {usernameField}
            <label for="email">Email</label>
            <input id="email" name="email" type="{(signIn ? "text" : "email")}" inputmode="email" required maxlength="254" autocomplete="username" spellcheck="false">
            <label for="password">Password</label>
            <input id="password" name="password" type="password" required minlength="{PortalAccounts.MinimumPasswordLength}" maxlength="{PortalAccounts.MaximumPasswordLength}" autocomplete="{(signIn ? "current-password" : "new-password")}">
            {codeField}
            """;
        var body = manage ? $"""
            <p>Signed in as <strong>{HtmlEncoder.Default.Encode(PortalAccounts.DisplayNameFor(context.User))}</strong></p>
            <p>{HtmlEncoder.Default.Encode(context.User.Identity?.Name ?? "")}</p>
            {(context.User.IsInRole("Owner") ? "<a href=\"/user-control\">User Control</a>" : "")}
            <form method="post" action="/account/sign-out">{antiforgery}
                <button type="submit" class="secondary">Sign out</button>
            </form>
            <a href="/">Back to dashboard</a>
            """ : submitted ? """
            <p>The portal owner will review your request. You can sign in once they approve it.</p>
            <a href="/account/sign-in">Back to sign in</a>
            """ : $"""
            {(setup ? "<p>The owner account will receive the existing knowledge base. Get the setup code from the Docker host.</p>" :
                signIn ? "<p>Access your private knowledge workspace.</p>" :
                "<p>Choose a username for your profile. You can sign in with your email after the portal owner approves your request.</p>")}
            {errorMarkup}
            <form method="post" action="{action}">{antiforgery}{fields}
                <button type="submit">{title}</button>
            </form>
            {(setup ? "" : signIn ? "<a href=\"/account/join\">Request an account</a>" : "<a href=\"/account/sign-in\">Already have an account?</a>")}
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
