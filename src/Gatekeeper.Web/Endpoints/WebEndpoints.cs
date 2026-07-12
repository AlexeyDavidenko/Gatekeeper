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

        // Decision form posts — require auth + a valid antiforgery token.
        var apps = app.MapGroup("/applications").RequireAuthorization();
        apps.MapPost("/{id:long}/approve", (long id, HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct)
            => DecideAsync(id, approve: true, ctx, api, af, ct));
        apps.MapPost("/{id:long}/reject", (long id, HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct)
            => DecideAsync(id, approve: false, ctx, api, af, ct));

        // Ban/Mute/Kick/Warn — separate from approve/reject above, acts on the Telegram user, not
        // the application. Evidence upload (optional) is a second call, only if a file was chosen —
        // see AttachEvidenceHandler's doc comment for why creating the action and attaching
        // evidence are deliberately independent operations.
        apps.MapPost("/{id:long}/moderate", async (long id, HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct) =>
        {
            await ValidateAsync(af, ctx);
            var form = await ctx.Request.ReadFormAsync(ct);
            if (!long.TryParse(form["telegramUserId"], out var telegramUserId) || !long.TryParse(form["chatId"], out var chatId))
                return Results.BadRequest("Missing telegramUserId/chatId.");

            var (actingUserId, actingUserName) = CurrentUser(ctx);
            var actionId = await api.ModerateAsync(telegramUserId, new ModerationRequest(
                form["action"].ToString(), form["reason"], null, chatId, actingUserId, actingUserName, "Web", null), ct);

            var evidence = form.Files["evidence"];
            if (evidence is { Length: > 0 })
            {
                await using var stream = evidence.OpenReadStream();
                await api.AttachEvidenceAsync(actionId, stream, evidence.ContentType, evidence.FileName, actingUserId, ct);
            }

            return Results.Redirect($"applications/{id}");
        });

        // Question management form posts — same auth/antiforgery shape as decisions above.
        // NB: these must NOT share a URL with a @page route (Razor component endpoints match
        // any HTTP verb on their route template) — "/questions/{id}/edit" collided with
        // QuestionEdit.razor's own "/questions/{Id:long}/edit" page route and threw
        // AmbiguousMatchException on every submit. Suffixed "/create" and "/save" instead.
        var questions = app.MapGroup("/questions").RequireAuthorization();
        questions.MapPost("/create", async (HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct) =>
        {
            await ValidateAsync(af, ctx);
            var form = await ctx.Request.ReadFormAsync(ct);
            var promptText = RichTextSanitizer.Sanitize(form["promptText"]);
            var type = form["type"].ToString() is { Length: > 0 } t ? t : "Text";
            await api.CreateQuestionAsync(type, promptText, form["isRequired"] == "true", ParseOptions(form, type), ct);
            return Results.Redirect("/questions");
        });
        questions.MapPost("/{id:long}/save", async (long id, HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct) =>
        {
            await ValidateAsync(af, ctx);
            var form = await ctx.Request.ReadFormAsync(ct);
            var promptText = RichTextSanitizer.Sanitize(form["promptText"]);
            var type = form["type"].ToString() is { Length: > 0 } t ? t : "Text";
            await api.EditQuestionAsync(
                id, type, promptText, form["isRequired"] == "true", ParseOptions(form, type),
                form["isActive"] == "true", ct);
            return Results.Redirect("/questions");
        });
        questions.MapPost("/{id:long}/move-up", (long id, HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct)
            => MoveAsync(id, up: true, ctx, api, af, ct));
        questions.MapPost("/{id:long}/move-down", (long id, HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct)
            => MoveAsync(id, up: false, ctx, api, af, ct));
        questions.MapPost("/{id:long}/deactivate", async (long id, HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct) =>
        {
            await ValidateAsync(af, ctx);
            await api.DeactivateQuestionAsync(id, ct);
            return Results.Redirect("/questions");
        });

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
        history.MapPost("/{id:long}/unarchive", async (long id, HttpContext ctx, AdminApiClient api, IAntiforgery af, CancellationToken ct) =>
        {
            await ValidateAsync(af, ctx);
            await api.UnarchiveModerationActionAsync(id, ct);
            return Results.Redirect("/history?includeArchived=true");
        });

        return app;
    }

    private static async Task<IResult> MoveAsync(
        long id, bool up, HttpContext ctx, AdminApiClient api, IAntiforgery antiforgery, CancellationToken ct)
    {
        await ValidateAsync(antiforgery, ctx);
        await api.MoveQuestionAsync(id, up, ct);
        return Results.Redirect("/questions");
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
        return Results.Redirect(outcome == DecisionOutcome.AlreadyDecided ? "/queue?notice=conflict" : "/queue");
    }

    private static IReadOnlyList<string>? ParseOptions(IFormCollection form, string type)
    {
        if (type is not ("SingleChoice" or "MultiChoice")) return null;
        var values = form["options"].Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        return values.Count > 0 ? values : null;
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
