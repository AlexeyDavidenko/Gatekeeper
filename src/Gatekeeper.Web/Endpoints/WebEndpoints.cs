namespace Gatekeeper.Web.Endpoints;

using System.Security.Claims;
using Gatekeeper.Web.Auth;
using Gatekeeper.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

public static class WebEndpoints
{
    public static IEndpointRouteBuilder MapWebEndpoints(this IEndpointRouteBuilder app)
    {
        // Telegram Login Widget callback (data-auth-url redirect mode).
        app.MapGet("/auth/telegram", async (
            HttpContext ctx, IConfiguration config, TelegramAdminChecker adminChecker, CancellationToken ct) =>
        {
            var fields = ctx.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            var botToken = config["Telegram:BotToken"]!;

            if (!TelegramLoginValidator.TryValidate(fields, botToken, TimeSpan.FromMinutes(10), out var data) || data is null)
                return Results.Redirect("/login?error=invalid");

            var adminChatId = long.Parse(config["Telegram:AdminChatId"]!);
            if (!await adminChecker.IsAdminAsync(adminChatId, data.Id, ct))
                return Results.Redirect("/login?error=forbidden");

            var displayName = $"{data.FirstName} {data.LastName}".Trim();
            if (string.IsNullOrEmpty(displayName)) displayName = data.Username ?? data.Id.ToString();

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, data.Id.ToString()),
                new(ClaimTypes.Name, displayName),
                new("tg_username", data.Username ?? string.Empty),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });

            return Results.Redirect("/");
        });

        app.MapPost("/auth/logout", async (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            await ValidateAsync(antiforgery, ctx);
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        // Decision form posts — require auth + a valid antiforgery token.
        var apps = app.MapGroup("/applications").RequireAuthorization();
        apps.MapPost("/{id:long}/approve", (long id, HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct)
            => DecideAsync(id, approve: true, ctx, api, af, ct));
        apps.MapPost("/{id:long}/reject", (long id, HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct)
            => DecideAsync(id, approve: false, ctx, api, af, ct));

        return app;
    }

    private static async Task<IResult> DecideAsync(
        long id, bool approve, HttpContext ctx, AdminApiClient api, IAntiforgery antiforgery, CancellationToken ct)
    {
        await ValidateAsync(antiforgery, ctx);

        var form = await ctx.Request.ReadFormAsync(ct);
        if (!uint.TryParse(form["rowVersion"], out var rowVersion))
            return Results.BadRequest("Missing rowVersion.");

        var (userId, name) = CurrentUser(ctx);
        var outcome = await api.DecideAsync(id, rowVersion, approve, form["reason"], userId, name, ct);
        return Results.Redirect(outcome == DecisionOutcome.AlreadyDecided ? "/?notice=conflict" : "/");
    }

    private static async Task ValidateAsync(IAntiforgery antiforgery, HttpContext ctx)
    {
        try { await antiforgery.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException) { throw new BadHttpRequestException("Invalid antiforgery token."); }
    }

    private static (long UserId, string? Name) CurrentUser(HttpContext ctx)
    {
        var id = long.TryParse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : 0;
        return (id, ctx.User.Identity?.Name);
    }
}
