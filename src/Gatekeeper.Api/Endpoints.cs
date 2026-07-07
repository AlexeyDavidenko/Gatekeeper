using Gatekeeper.Application;
using Gatekeeper.Domain;
using Gatekeeper.Infrastructure;

namespace Gatekeeper.Api;

using System.Text.Json;
using System.Text.RegularExpressions;
using Gatekeeper.Application.Applications;
using Gatekeeper.Application.Tenants;
using Gatekeeper.Contracts;
using Gatekeeper.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

/// <summary>Cheap shared-secret gate. The API is only reachable on the private Docker network.</summary>
public sealed class InternalApiKeyMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly string _key = config["Internal:ApiKey"]!;

    public async Task Invoke(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/alive"))
        { await next(ctx); return; }

        if (!ctx.Request.Headers.TryGetValue("X-Internal-Key", out var key) || key != _key)
        { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }

        await next(ctx);
    }
}

/// <summary>Resolves the X-Tenant-Id header into the scoped ITenantContext. /internal/* is tenant-agnostic.</summary>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext ctx, TenantContext tenantContext, CatalogDbContext catalog)
    {
        if (ctx.Request.Path.StartsWithSegments("/internal") ||
            ctx.Request.Path.StartsWithSegments("/health") ||
            ctx.Request.Path.StartsWithSegments("/alive"))
        { await next(ctx); return; }

        if (!ctx.Request.Headers.TryGetValue("X-Tenant-Id", out var raw) || !long.TryParse(raw, out var tenantId))
        { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; await ctx.Response.WriteAsync("Missing X-Tenant-Id."); return; }

        var tenant = await catalog.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive);
        if (tenant is null)
        { ctx.Response.StatusCode = StatusCodes.Status404NotFound; await ctx.Response.WriteAsync("Unknown tenant."); return; }

        tenantContext.Set(tenant.Id, tenant.Slug, tenant.DatabaseName);
        await next(ctx);
    }
}

public static class ApplicationsEndpoints
{
    public static IEndpointRouteBuilder MapApplicationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/applications");

        // Approve / reject — used by both the admin-group buttons and the website.
        group.MapPost("/{id:long}/decision", async (
            long id, DecideRequest body, HttpContext http,
            DecideApplicationHandler handler, CancellationToken ct) =>
        {
            // If-Match carries the row version for optimistic concurrency.
            var ifMatch = http.Request.Headers.IfMatch.ToString().Trim('"');
            if (!uint.TryParse(ifMatch, out var rowVersion))
                return Results.BadRequest("Missing or invalid If-Match header.");

            if (!Enum.TryParse<ActionSource>(body.Source, out var source))
                return Results.BadRequest("Invalid source.");

            try
            {
                await handler.HandleAsync(new DecideApplicationCommand(
                    id, body.Approve, body.Reason, body.ActingUserId, body.ActingUserName, source, rowVersion), ct);
                return Results.NoContent();
            }
            catch (ConcurrencyConflictException ex)
            {
                return Results.Conflict(ex.Message);  // 409 — the website defers to the Telegram-side decision
            }
        });

        // Create from a join request → returns the welcome + first question.
        group.MapPost("/", async (CreateApplicationRequest body, CreateApplicationHandler handler, CancellationToken ct) =>
        {
            var first = await handler.HandleAsync(new CreateApplicationCommand(
                body.ChatId, body.User.Id, body.User.IsBot, body.User.IsPremium,
                body.User.Username, body.User.FirstName, body.User.LastName, body.User.LanguageCode,
                body.UserChatId, body.InviteLink, body.Bio, body.User.PhotoFileId), ct);
            return Results.Ok(new NextQuestionDto(first.QuestionId, first.Prompt, first.Type, first.Completed));
        });

        // Submit a survey answer (active application resolved by user id) → next question or completion.
        group.MapPost("/answers", async (SubmitAnswerRequest body, SubmitAnswerHandler handler, CancellationToken ct) =>
        {
            var step = await handler.HandleAsync(new SubmitAnswerCommand(body.TelegramUserId, body.Text), ct);
            return Results.Ok(new NextQuestionDto(step.QuestionId, step.Prompt, step.Type ?? nameof(QuestionType.Text), step.Completed));
        });

        // Admin-group button press → decision. Data is "appr:{id}" or "rej:{id}".
        group.MapPost("/callback", async (AdminCallbackRequest body, DecideApplicationHandler handler, CancellationToken ct) =>
        {
            var parts = body.Data.Split(':', 2);
            if (parts.Length != 2 || !long.TryParse(parts[1], out var appId))
                return Results.BadRequest("Malformed callback data.");

            try
            {
                await handler.HandleAsync(new DecideApplicationCommand(
                    appId, parts[0] == "appr", null, body.ActingUserId, body.ActingUserName,
                    ActionSource.TelegramGroup, ExpectedRowVersion: null), ct);
                return Results.NoContent();
            }
            catch (ConcurrencyConflictException)
            {
                return Results.NoContent();  // already decided (likely from the website) — Telegram-side is authoritative
            }
        });

        // Queue for the admin site (defaults to applications awaiting review).
        group.MapGet("/", async (string? status, TenantDbContext db, CancellationToken ct) =>
        {
            var target = Enum.TryParse<ApplicationStatus>(status, ignoreCase: true, out var parsed)
                ? parsed
                : ApplicationStatus.AwaitingReview;

            var apps = await db.Applications.AsNoTracking()
                .Where(a => a.Status == target)
                .OrderByDescending(a => a.SubmittedAt)
                .Take(200)
                .ToListAsync(ct);

            var ids = apps.Select(a => a.TelegramUserId).ToList();
            var users = await db.Users.AsNoTracking()
                .Where(u => ids.Contains(u.TelegramUserId))
                .ToDictionaryAsync(u => u.TelegramUserId, ct);

            var result = apps.Select(a =>
            {
                users.TryGetValue(a.TelegramUserId, out var u);
                return new ApplicationSummary(a.Id, a.TelegramUserId, u?.Username, Display(u),
                    a.Status.ToString(), a.SubmittedAt);
            }).ToList();

            return Results.Ok(result);
        });

        // Full card with answers + the row version the site echoes back as If-Match.
        group.MapGet("/{id:long}", async (long id, TenantDbContext db, CancellationToken ct) =>
        {
            var a = await db.Applications.AsNoTracking()
                .Include(x => x.Answers)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();

            var u = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TelegramUserId == a.TelegramUserId, ct);

            var answers = a.Answers
                .OrderBy(x => x.PositionSnapshot)
                .Select(x => new AnswerDto(x.PromptSnapshot, x.TypeSnapshot.ToString(), x.PositionSnapshot, x.Text))
                .ToList();

            return Results.Ok(new ApplicationCard(a.Id, a.TelegramUserId, u?.Username, Display(u),
                a.Status.ToString(), a.CreatedAt, a.SubmittedAt, a.RowVersion, answers));
        });

        return app;
    }

    internal static string? Display(TelegramUser? user) =>
        user is null ? null : $"{user.FirstName} {user.LastName}".Trim() is { Length: > 0 } name ? name : null;
}

public static class QuestionsEndpoints
{
    public static IEndpointRouteBuilder MapQuestionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/questions");

        group.MapGet("/", async (TenantDbContext db, CancellationToken ct) =>
        {
            var qs = await db.Questions.AsNoTracking().OrderBy(q => q.Position).ToListAsync(ct);
            return Results.Ok(qs.Select(ToDto).ToList());
        });

        group.MapGet("/{id:long}", async (long id, TenantDbContext db, CancellationToken ct) =>
        {
            var q = await db.Questions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return q is null ? Results.NotFound() : Results.Ok(ToDto(q));
        });

        group.MapPost("/", async (CreateQuestionRequest body, CreateQuestionHandler handler, CancellationToken ct) =>
        {
            var id = await handler.HandleAsync(new CreateQuestionCommand(body.PromptText, body.IsRequired), ct);
            return Results.Ok(new { Id = id });
        });

        group.MapPut("/{id:long}", async (long id, EditQuestionRequest body, EditQuestionHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new EditQuestionCommand(id, body.PromptText, body.IsRequired), ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:long}/move-up", async (long id, MoveQuestionHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new MoveQuestionCommand(id, Up: true), ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:long}/move-down", async (long id, MoveQuestionHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new MoveQuestionCommand(id, Up: false), ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:long}/deactivate", async (long id, DeactivateQuestionHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new DeactivateQuestionCommand(id), ct);
            return Results.NoContent();
        });

        return app;
    }

    private static QuestionDto ToDto(Question q) => new(q.Id, q.Position, q.PromptText, q.IsRequired, q.IsActive);
}

public static class ModerationEndpoints
{
    public static IEndpointRouteBuilder MapModerationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/users/{telegramUserId:long}/moderation", async (
            long telegramUserId, ModerationRequest body, ModerateUserHandler handler, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ModerationActionType>(body.Action, ignoreCase: true, out var action))
                return Results.BadRequest("Invalid action.");
            if (!Enum.TryParse<ActionSource>(body.Source, ignoreCase: true, out var source))
                return Results.BadRequest("Invalid source.");

            await handler.HandleAsync(new ModerateUserCommand(
                telegramUserId, body.ChatId, action, body.Reason, body.Notes,
                body.ActingUserId, body.ActingUserName, source, body.ExpiresAt), ct);
            return Results.NoContent();
        });

        // Audit log for the admin site — gated client-side to the elevated (Owner) role.
        app.MapGet("/moderation", async (TenantDbContext db, CancellationToken ct) =>
        {
            var actions = await db.ModerationActions.AsNoTracking()
                .OrderByDescending(a => a.CreatedAt)
                .Take(200)
                .ToListAsync(ct);

            var ids = actions.Select(a => a.TelegramUserId).Distinct().ToList();
            var users = await db.Users.AsNoTracking()
                .Where(u => ids.Contains(u.TelegramUserId))
                .ToDictionaryAsync(u => u.TelegramUserId, ct);

            var result = actions.Select(a =>
            {
                users.TryGetValue(a.TelegramUserId, out var u);
                return new ModerationLogEntry(
                    a.Id, a.TelegramUserId, u?.Username, ApplicationsEndpoints.Display(u), a.ApplicationId,
                    a.Action.ToString(), a.Reason, a.Notes, a.PerformedByUserId, a.PerformedByName,
                    a.Source.ToString(), a.CreatedAt);
            }).ToList();

            return Results.Ok(result);
        });

        return app;
    }
}

public static class InternalEndpoints
{
    // Tenant-agnostic control-plane surface (the tenant middleware skips /internal).
    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/internal");

        // Resolve a tenant by an incoming chat id — the bot calls this to route updates.
        g.MapGet("/tenants/resolve", async (long chatId, CatalogDbContext catalog, CancellationToken ct) =>
        {
            var chat = await catalog.TenantChats.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            return chat is null
                ? Results.NotFound()
                : Results.Ok(new { chat.TenantId, Role = chat.Role.ToString() });
        });

        // Rehydration: the bot calls this once at startup to repopulate its in-memory TenantRouter —
        // otherwise a restart (deploy, crash, power/internet outage) silently strands anyone
        // mid-survey, since a DM carries no chat->tenant mapping on its own.
        g.MapGet("/applications/in-progress", async (
            CatalogDbContext catalog, TenantDbContextFactory factory, CancellationToken ct) =>
        {
            var result = new List<InProgressApplicant>();
            var tenants = await catalog.Tenants.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
            foreach (var tenant in tenants)
            {
                await using var db = factory.ForDatabase(tenant.DatabaseName);
                var userIds = await db.Applications.AsNoTracking()
                    .Where(a => a.Status == ApplicationStatus.SurveyOffered || a.Status == ApplicationStatus.InSurvey)
                    .Select(a => a.TelegramUserId)
                    .ToListAsync(ct);
                result.AddRange(userIds.Select(userId => new InProgressApplicant(tenant.Id, userId)));
            }
            return Results.Ok(result);
        });

        // Outbox drain: scan active tenants, claim Pending commands, hand them to the bot.
        g.MapGet("/telegram-commands/pending", async (
            int? batch, CatalogDbContext catalog, TenantDbContextFactory factory, CancellationToken ct) =>
        {
            var take = batch is > 0 ? batch.Value : 32;
            var result = new List<PendingCommand>();

            var tenants = await catalog.Tenants.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
            foreach (var tenant in tenants)
            {
                if (result.Count >= take) break;
                await using var db = factory.ForDatabase(tenant.DatabaseName);

                var pending = await db.TelegramCommands
                    .Where(c => c.Status == TelegramCommandStatus.Pending)
                    .OrderBy(c => c.Id)
                    .Take(take - result.Count)
                    .ToListAsync(ct);
                if (pending.Count == 0) continue;

                foreach (var cmd in pending) cmd.MarkInFlight();
                await db.SaveChangesAsync(ct);   // claim them so a second drain pass can't double-send

                foreach (var cmd in pending)
                {
                    var p = JsonSerializer.Deserialize<TelegramCommandPayload>(cmd.PayloadJson)!;
                    result.Add(new PendingCommand(
                        tenant.Id, cmd.Id, cmd.Type.ToString(),
                        p.ChatId ?? 0, p.UserId ?? 0, p.Text, p.MessageId,
                        p.Buttons?.Select(b => new CommandButton(b.Text, b.CallbackData)).ToList(),
                        p.PhotoFileId, p.IsPhotoCaption));
                }
            }

            return Results.Ok(result);
        });

        // Ack: the bot reports the Telegram outcome so the command becomes terminal.
        g.MapPost("/telegram-commands/{tenantId:long}/{commandId:long}/result", async (
            long tenantId, long commandId, CommandResult body,
            CatalogDbContext catalog, TenantDbContextFactory factory, CancellationToken ct) =>
        {
            var tenant = await catalog.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null) return Results.NotFound();

            await using var db = factory.ForDatabase(tenant.DatabaseName);
            var cmd = await db.TelegramCommands.FirstOrDefaultAsync(c => c.Id == commandId, ct);
            if (cmd is null) return Results.NotFound();

            if (body.Success)
            {
                cmd.MarkSucceeded(DateTimeOffset.UtcNow);

                // The moderation card's message_id — captured so a later decision can EditMessage it.
                if (cmd.Type == TelegramCommandType.SendAdminCard && body.MessageId is { } messageId && cmd.ApplicationId is { } applicationId)
                {
                    var application = await db.Applications.FirstOrDefaultAsync(a => a.Id == applicationId, ct);
                    var payload = JsonSerializer.Deserialize<TelegramCommandPayload>(cmd.PayloadJson)!;
                    application?.SetAdminCardMessageId(messageId, hasPhoto: payload.PhotoFileId is not null);
                }
            }
            else cmd.MarkFailed(body.Error ?? "unknown", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        // Idempotent tenant provisioning: creates + migrates the tenant's database and registers
        // it (with its two chats) in the Catalog. Safe to call again for an already-known slug.
        g.MapPost("/tenants/provision", async (
            ProvisionTenantRequest body, ProvisionTenantHandler handler, CancellationToken ct) =>
        {
            if (!TenantSlugPattern.IsMatch(body.Slug))
                return Results.BadRequest("Slug must be lowercase letters, digits, underscores, or hyphens.");

            var result = await handler.HandleAsync(new ProvisionTenantCommand(
                body.Slug, body.Name, body.MainChatId, body.AdminChatId, body.MainChatTitle, body.AdminChatTitle), ct);

            return Results.Ok(new ProvisionTenantResponse(result.TenantId, result.DatabaseName, result.WasCreated));
        });

        return app;
    }

    private static readonly Regex TenantSlugPattern = new("^[a-z0-9_-]+$", RegexOptions.Compiled);
}
