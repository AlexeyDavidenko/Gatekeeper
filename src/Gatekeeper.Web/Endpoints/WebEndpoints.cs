namespace Gatekeeper.Web.Endpoints;

using System.Security.Claims;
using Gatekeeper.Contracts;
using Gatekeeper.Web.Auth;
using Gatekeeper.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Telegram.Bot;

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
            var role = await adminChecker.GetRoleAsync(adminChatId, data.Id, ct);
            if (role is null)
                return Results.Redirect("/login?error=forbidden");

            var displayName = $"{data.FirstName} {data.LastName}".Trim();
            if (string.IsNullOrEmpty(displayName)) displayName = data.Username ?? data.Id.ToString();

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, data.Id.ToString()),
                new(ClaimTypes.Name, displayName),
                new("tg_username", data.Username ?? string.Empty),
                new(ClaimTypes.Role, role.Value.ToString()),
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

        // Language switcher: a real HTTP redirect (not an in-circuit navigation) so the next page
        // load always starts a fresh Blazor circuit — see LocaleContext's doc comment for why that
        // matters. "returnUrl" defaults to "/" and is never trusted as an absolute/external URL.
        app.MapGet("/lang/{code}", (string code, string? returnUrl, HttpContext ctx) =>
        {
            ctx.Response.Cookies.Append("gk_lang", code is "en" ? "en" : "ru", new CookieOptions
            {
                HttpOnly = false,   // read by no JS today, but harmless to allow and matches a future theme-style toggle
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
            });
            var target = returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/";
            return Results.Redirect(target);
        });

        // Avatar proxy: PhotoFileId is a Telegram-internal file_id, not a browser-usable URL —
        // resolve it (getFile) and stream the bytes through, using the bot token server-side.
        // Cached client-side since profile photos rarely change and a fresh getFile is a live API call.
        app.MapGet("/avatar/{fileId}", async (string fileId, ITelegramBotClient bot, HttpContext ctx, CancellationToken ct) =>
        {
            try
            {
                using var stream = new MemoryStream();
                await bot.GetInfoAndDownloadFile(fileId, stream, ct);
                ctx.Response.Headers.CacheControl = "private, max-age=3600";
                return Results.File(stream.ToArray(), "image/jpeg");
            }
            catch
            {
                return Results.NotFound();
            }
        }).RequireAuthorization();

        // Approve/Reject now opens ApplicationDetailDialog (from Queue.razor/History.razor) or uses
        // its own inline grid actions, both calling AdminApiClient.DecideAsync directly in-circuit —
        // no form post, no route needed for them. Same for Ban/Mute/Kick/Warn (ModerationDialog.razor,
        // AdminApiClient.ModerateAsync/AttachEvidenceAsync).

        // Question management (create/edit/move/deactivate) now all calls AdminApiClient directly
        // in-circuit from Questions.razor/QuestionDialog.razor — no form posts, no routes needed.

        // Moderation-log archiving form posts (History.razor, Owner-only control) — same
        // auth/antiforgery shape as decisions/questions above.
        var history = app.MapGroup("/history").RequireAuthorization();
        history.MapPost("/archive", async (HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct) =>
        {
            await ValidateAsync(af, ctx);
            var form = await ctx.Request.ReadFormAsync(ct);
            if (!DateTimeOffset.TryParse(form["olderThan"], out var olderThan))
                return Results.Redirect("/history?includeArchived=true");
            var count = await api.ArchiveModerationActionsAsync(olderThan, ct);
            return Results.Redirect($"/history?includeArchived=true&archived={count}");
        });
        // Single/bulk restore now call AdminApiClient directly in-circuit from History.razor's
        // MudDataGrid row action / bulk-select button — no form post, no route needed for them.

        // /translations editor (Owner-only, enforced by the page itself — this form post shares its
        // route prefix but the antiforgery+auth cookie already gates it the same way). Refreshes
        // this Web instance's own TranslationCache immediately so the editor's next load reflects
        // the change right away — other running Bot/Web processes pick it up on their own next
        // periodic refresh (60s), an accepted small lag, same tradeoff as everywhere else this
        // migration uses a periodically-refreshed cache.
        app.MapGroup("/translations").RequireAuthorization(policy => policy.RequireRole("Owner"))
            .MapPost("/save", async (HttpContext ctx, AdminApiClient api, TranslationCache cache, IAntiforgery af, CancellationToken ct) =>
            {
                await ValidateAsync(af, ctx);
                var form = await ctx.Request.ReadFormAsync(ct);
                var key = form["key"].ToString();
                if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest("Missing key.");

                await api.UpdateTranslationAsync(key, "ru", form["ru"].ToString(), ct);
                await api.UpdateTranslationAsync(key, "en", form["en"].ToString(), ct);
                cache.Load(await api.GetTranslationsAsync(ct));

                return Results.Redirect("/translations");
            });

        return app;
    }

    private static async Task ValidateAsync(IAntiforgery antiforgery, HttpContext ctx)
    {
        try { await antiforgery.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException) { throw new BadHttpRequestException("Invalid antiforgery token."); }
    }
}
