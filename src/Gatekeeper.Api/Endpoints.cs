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
    // word_similarity's own GUC default (0.6) is tuned for full-length comparisons; a single-
    // character typo on a short 4-8 char first/last name can legitimately knock the score into the
    // 0.4-0.55 range, so the default would under-match exactly the case this threshold exists for.
    // Not lower than this, to avoid admitting unrelated names — starting point, sanity-check against
    // real names if search quality ever needs retuning.
    private const double FuzzyNameSimilarityThreshold = 0.4;

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

        // Admin-driven mid-survey cancel — bot's "/cancel {id}" command. Distinct from /decision:
        // no admin card exists yet to edit (only sent once the survey reaches AwaitingReview) and no
        // competing-decision race to guard with a rowVersion, so this is a plain command endpoint,
        // not routed through /callback (which carries ChatId/MessageId for editing a card).
        group.MapPost("/{id:long}/cancel", async (
            long id, CancelApplicationRequest body, CancelApplicationHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new CancelApplicationCommand(id, body.ActingUserId, body.ActingUserName), ct);
            return Results.NoContent();
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
        // 409 if the survey hasn't been explicitly started yet (see StartSurveyHandler) — a stray
        // message sent before tapping a language-picker button, not a real answer.
        group.MapPost("/answers", async (SubmitAnswerRequest body, SubmitAnswerHandler handler, CancellationToken ct) =>
        {
            try
            {
                var step = await handler.HandleAsync(
                    new SubmitAnswerCommand(body.TelegramUserId, body.Text, body.SelectedOptionIndexes), ct);
                return Results.Ok(new NextQuestionDto(
                    step.QuestionId, step.Prompt, step.Type ?? nameof(QuestionType.Text), step.Completed,
                    step.LanguageCode, step.Options));
            }
            catch (SurveyNotStartedException)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }
        });

        // Admin-group button press. Data is "appr:{id}" / "rej:{id}" (decision) or
        // "ans:{id}" / "hideans:{id}" (show/hide answers toggle on the card).
        group.MapPost("/callback", async (
            AdminCallbackRequest body, DecideApplicationHandler decideHandler,
            ToggleCardAnswersHandler toggleHandler, CancellationToken ct) =>
        {
            var parts = body.Data.Split(':', 2);
            if (parts.Length != 2 || !long.TryParse(parts[1], out var appId))
                return Results.BadRequest("Malformed callback data.");

            switch (parts[0])
            {
                case "appr" or "rej":
                    try
                    {
                        await decideHandler.HandleAsync(new DecideApplicationCommand(
                            appId, parts[0] == "appr", null, body.ActingUserId, body.ActingUserName,
                            ActionSource.TelegramGroup, ExpectedRowVersion: null), ct);
                    }
                    catch (ConcurrencyConflictException)
                    {
                        // already decided (likely from the website) — Telegram-side is authoritative
                    }
                    return Results.NoContent();

                case "ans" or "hideans":
                    await toggleHandler.HandleAsync(new ToggleCardAnswersCommand(appId, Show: parts[0] == "ans"), ct);
                    return Results.NoContent();

                default:
                    return Results.BadRequest("Unknown callback action.");
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
                    a.Status.ToString(), a.SubmittedAt, u?.PhotoFileId);
            }).ToList();

            return Results.Ok(result);
        });

        // Lookup for the bot's admin "/find" command — matches username/first/last name regardless
        // of status (unlike the queue above, which defaults to AwaitingReview only). Username stays
        // exact-substring only (handles like "@x7k2z" aren't natural-language names — fuzzy-matching
        // them mostly adds noise); first/last name also gets a trigram word_similarity pass against
        // NameNormalized so a typo'd or transliterated name still finds the right applicant. Note:
        // this is the function-call form (word_similarity(a,b) >= threshold), which Postgres's planner
        // does NOT rewrite into the indexed operator form (%/<%) — the existing GIN gin_trgm_ops index
        // on NameNormalized does not accelerate this query, it's a per-row scan. Accepted at current
        // per-tenant data volumes (hundreds-to-low-thousands of rows); if it ever needs to scale, the
        // fix is switching to the TrigramsAreWordSimilar operator form + a SET LOCAL
        // pg_trgm.word_similarity_threshold inside a transaction, not attempted here.
        group.MapGet("/search", async (string? q, TenantDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(new List<ApplicationSummary>());

            var pattern = $"%{q.Trim()}%";
            var normalizedQuery = q.Trim().ToLowerInvariant();
            var matchingUserIds = await db.Users.AsNoTracking()
                .Where(u => EF.Functions.ILike(u.Username ?? "", pattern) ||
                            EF.Functions.ILike(u.FirstName ?? "", pattern) ||
                            EF.Functions.ILike(u.LastName ?? "", pattern) ||
                            EF.Functions.TrigramsWordSimilarity(normalizedQuery, u.NameNormalized) >= FuzzyNameSimilarityThreshold)
                .Select(u => u.TelegramUserId)
                .ToListAsync(ct);

            var apps = await db.Applications.AsNoTracking()
                .Where(a => matchingUserIds.Contains(a.TelegramUserId))
                .OrderByDescending(a => a.SubmittedAt ?? a.CreatedAt)
                .Take(10)
                .ToListAsync(ct);

            var users = await db.Users.AsNoTracking()
                .Where(u => matchingUserIds.Contains(u.TelegramUserId))
                .ToDictionaryAsync(u => u.TelegramUserId, ct);

            var result = apps.Select(a =>
            {
                users.TryGetValue(a.TelegramUserId, out var u);
                return new ApplicationSummary(a.Id, a.TelegramUserId, u?.Username, Display(u),
                    a.Status.ToString(), a.SubmittedAt, u?.PhotoFileId);
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

            return Results.Ok(new ApplicationCard(a.Id, a.TelegramUserId, a.MainChatId, u?.Username, Display(u),
                a.Status.ToString(), a.CreatedAt, a.SubmittedAt, a.RowVersion, answers, u?.Bio, u?.PhotoFileId));
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
            if (!Enum.TryParse<QuestionType>(body.Type, out var type))
                return Results.BadRequest("Invalid question type.");

            var configJson = ChoiceOptions.SerializeQuestionOptions(body.Options);
            var configJsonEn = ChoiceOptions.SerializeQuestionOptions(body.OptionsEn);
            var id = await handler.HandleAsync(new CreateQuestionCommand(
                type, body.PromptText, body.IsRequired, configJson, body.PromptTextEn, configJsonEn), ct);
            return Results.Ok(new { Id = id });
        });

        group.MapPut("/{id:long}", async (long id, EditQuestionRequest body, EditQuestionHandler handler, CancellationToken ct) =>
        {
            if (!Enum.TryParse<QuestionType>(body.Type, out var type))
                return Results.BadRequest("Invalid question type.");

            var configJson = ChoiceOptions.SerializeQuestionOptions(body.Options);
            var configJsonEn = ChoiceOptions.SerializeQuestionOptions(body.OptionsEn);
            await handler.HandleAsync(new EditQuestionCommand(
                id, type, body.PromptText, body.IsRequired, configJson, body.IsActive, body.PromptTextEn, configJsonEn), ct);
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

    private static QuestionDto ToDto(Question q) => new(
        q.Id, q.Position, q.PromptText, q.IsRequired, q.IsActive,
        q.Type.ToString(), ChoiceOptions.ParseQuestionOptions(q.ConfigJson),
        q.PromptTextEn, ChoiceOptions.ParseQuestionOptions(q.ConfigJsonEn));
}

public static class ModerationEndpoints
{
    public static IEndpointRouteBuilder MapModerationEndpoints(this IEndpointRouteBuilder app)
    {
        // Bot's language picker persists the applicant's explicit choice here — so a decision DM
        // sent much later (possibly by a website approve/reject, a different process entirely)
        // still picks the right language.
        app.MapPost("/users/{telegramUserId:long}/language", async (
            long telegramUserId, SetLanguageRequest body, SetUserLanguageHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new SetUserLanguageCommand(telegramUserId, body.LanguageCode), ct);
            return Results.NoContent();
        });

        // Explicit "start the survey" trigger for the language-picker's "lang:go"/"lang:ru"/"lang:en"
        // callbacks — see StartSurveyHandler for why this must be a distinct, deliberate action
        // rather than something a stray message can trigger as a side effect.
        app.MapPost("/users/{telegramUserId:long}/start-survey", async (
            long telegramUserId, StartSurveyHandler handler, CancellationToken ct) =>
        {
            var step = await handler.HandleAsync(new StartSurveyCommand(telegramUserId), ct);
            return Results.Ok(new NextQuestionDto(
                step.QuestionId, step.Prompt, step.Type ?? nameof(QuestionType.Text), step.Completed,
                step.LanguageCode, step.Options));
        });

        app.MapPost("/users/{telegramUserId:long}/moderation", async (
            long telegramUserId, ModerationRequest body, ModerateUserHandler handler, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ModerationActionType>(body.Action, ignoreCase: true, out var action))
                return Results.BadRequest("Invalid action.");
            if (!Enum.TryParse<ActionSource>(body.Source, ignoreCase: true, out var source))
                return Results.BadRequest("Invalid source.");

            var id = await handler.HandleAsync(new ModerateUserCommand(
                telegramUserId, body.ChatId, action, body.Reason, body.Notes,
                body.ActingUserId, body.ActingUserName, source, body.ExpiresAt), ct);
            return Results.Ok(new ModerateResponse(id));
        });

        // Evidence upload — deliberately separate from the moderation action itself (see
        // AttachEvidenceHandler's doc comment). multipart/form-data: "evidence" file part +
        // "actingUserId" field, same manual IFormCollection style WebEndpoints.cs already uses
        // for form posts, rather than introducing [FromForm] attribute binding as a second style.
        app.MapPost("/moderation/{id:long}/evidence", async (
            long id, HttpContext ctx, AttachEvidenceHandler handler, CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files["evidence"];
            if (file is null || file.Length == 0)
                return Results.BadRequest("Missing evidence file.");
            if (!long.TryParse(form["actingUserId"], out var actingUserId))
                return Results.BadRequest("Missing actingUserId.");

            await using var stream = file.OpenReadStream();
            await handler.HandleAsync(new AttachEvidenceCommand(id, stream, file.ContentType, actingUserId), ct);
            return Results.NoContent();
        });

        // Streams an evidence file back out — e.g. so an admin can view what they just uploaded.
        app.MapGet("/moderation-attachments/{attachmentId:long}", async (
            long attachmentId, TenantDbContext db, IEvidenceStorage storage, CancellationToken ct) =>
        {
            var attachment = await db.ModerationAttachments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
            if (attachment is null) return Results.NotFound();

            var stream = await storage.OpenReadAsync(attachment.StoragePath, ct);
            if (stream is null) return Results.NotFound();

            return Results.Stream(stream, attachment.ContentType);
        });

        // Audit log for the admin site — gated client-side to the elevated (Owner) role.
        // includeArchived defaults to false — archived rows are opt-in only (History.razor's
        // "показать заархивированные" toggle).
        app.MapGet("/moderation", async (bool? includeArchived, TenantDbContext db, CancellationToken ct) =>
        {
            var showArchived = includeArchived ?? false;
            var actions = await db.ModerationActions.AsNoTracking()
                .Where(a => showArchived || !a.IsArchived)
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
                    a.Source.ToString(), a.CreatedAt, a.IsArchived, a.ArchivedAt);
            }).ToList();

            return Results.Ok(result);
        });

        // Owner-triggered bulk cleanup (History.razor) — no automatic/scheduled purge exists.
        app.MapPost("/moderation/archive", async (
            ArchiveModerationActionsRequest body, ArchiveModerationActionsHandler handler, CancellationToken ct) =>
        {
            var count = await handler.HandleAsync(new ArchiveModerationActionsCommand(body.OlderThan), ct);
            return Results.Ok(new ArchiveModerationActionsResponse(count));
        });

        app.MapPost("/moderation/{id:long}/unarchive", async (
            long id, UnarchiveModerationActionHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new UnarchiveModerationActionCommand(id), ct);
            return Results.NoContent();
        });

        // Bulk restore for the History grid's multi-select — see UnarchiveModerationActionsHandler.
        app.MapPost("/moderation/unarchive-batch", async (
            UnarchiveModerationActionsRequest body, UnarchiveModerationActionsHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new UnarchiveModerationActionsCommand(body.ModerationActionIds), ct);
            return Results.NoContent();
        });

        return app;
    }
}

public static class SiteVisitsEndpoints
{
    public static IEndpointRouteBuilder MapSiteVisitsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/site-visits");

        // Written by Web's background flush worker — never on the request-serving path itself,
        // so a slow/failed write here never adds latency to (or breaks) an actual page load.
        group.MapPost("/", async (RecordSiteVisitRequest body, RecordSiteVisitHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new RecordSiteVisitCommand(
                body.UserId, body.UserName, body.SessionId, body.IpAddress, body.Path, body.Method,
                body.StatusCode, body.DurationMs, body.UserAgent, body.Referrer), ct);
            return Results.NoContent();
        });

        // Visitor log for the admin site — gated client-side to the elevated (Owner) role, same as
        // the moderation audit log used to be (see docs/changelog.md — replaced 2026-07-08).
        group.MapGet("/", async (ISiteVisitRepository visits, CancellationToken ct) =>
        {
            var recent = await visits.GetRecentAsync(200, ct);
            var result = recent.Select(v => new SiteVisitDto(
                v.Id, v.UserId, v.UserName, v.SessionId, v.IpAddress, v.Path, v.Method,
                v.StatusCode, v.DurationMs, v.UserAgent, v.Referrer, v.CreatedAt)).ToList();
            return Results.Ok(result);
        });

        return app;
    }
}

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard", async (TenantDbContext db, IClock clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var weekStart = todayStart.AddDays(-7);
            var trendStart = todayStart.AddDays(-13);   // 14 days inclusive of today, for the trend chart

            var pendingCount = await db.Applications.AsNoTracking()
                .CountAsync(a => a.Status == ApplicationStatus.AwaitingReview, ct);

            // Widened to 14 days (from the "this week" 7) so the same fetch also covers the trend
            // chart below — approvedWeek/rejectedWeek still filter down to the last 7 explicitly.
            var decisions = await db.ModerationActions.AsNoTracking()
                .Where(a => a.CreatedAt >= trendStart && !a.IsArchived &&
                    (a.Action == ModerationActionType.Approve || a.Action == ModerationActionType.Reject))
                .ToListAsync(ct);

            var approvedToday = decisions.Count(a => a.CreatedAt >= todayStart && a.Action == ModerationActionType.Approve);
            var rejectedToday = decisions.Count(a => a.CreatedAt >= todayStart && a.Action == ModerationActionType.Reject);
            var approvedWeek = decisions.Count(a => a.CreatedAt >= weekStart && a.Action == ModerationActionType.Approve);
            var rejectedWeek = decisions.Count(a => a.CreatedAt >= weekStart && a.Action == ModerationActionType.Reject);

            // One point per day, oldest first — filled explicitly via Range so a zero-activity day
            // still produces a (0, 0) entry instead of a gap (the frontend line chart needs a
            // continuous x-axis).
            var trend = Enumerable.Range(0, 14)
                .Select(i => trendStart.AddDays(i))
                .Select(day => new DashboardTrendPoint(
                    DateOnly.FromDateTime(day.UtcDateTime),
                    decisions.Count(a => a.CreatedAt.UtcDateTime.Date == day.UtcDateTime.Date && a.Action == ModerationActionType.Approve),
                    decisions.Count(a => a.CreatedAt.UtcDateTime.Date == day.UtcDateTime.Date && a.Action == ModerationActionType.Reject)))
                .ToList();

            var outboxPending = await db.TelegramCommands.AsNoTracking()
                .CountAsync(c => c.Status == TelegramCommandStatus.Pending, ct);
            var outboxInFlight = await db.TelegramCommands.AsNoTracking()
                .CountAsync(c => c.Status == TelegramCommandStatus.InFlight, ct);
            var outboxFailed = await db.TelegramCommands.AsNoTracking()
                .CountAsync(c => c.Status == TelegramCommandStatus.Failed, ct);

            var recent = await db.ModerationActions.AsNoTracking()
                .Where(a => !a.IsArchived &&
                    (a.Action == ModerationActionType.Approve || a.Action == ModerationActionType.Reject))
                .OrderByDescending(a => a.CreatedAt)
                .Take(10)
                .ToListAsync(ct);

            var ids = recent.Select(a => a.TelegramUserId).Distinct().ToList();
            var users = await db.Users.AsNoTracking()
                .Where(u => ids.Contains(u.TelegramUserId))
                .ToDictionaryAsync(u => u.TelegramUserId, ct);

            var recentDtos = recent.Select(a =>
            {
                users.TryGetValue(a.TelegramUserId, out var u);
                return new ModerationLogEntry(
                    a.Id, a.TelegramUserId, u?.Username, ApplicationsEndpoints.Display(u), a.ApplicationId,
                    a.Action.ToString(), a.Reason, a.Notes, a.PerformedByUserId, a.PerformedByName,
                    a.Source.ToString(), a.CreatedAt, a.IsArchived, a.ArchivedAt);
            }).ToList();

            return Results.Ok(new DashboardSummary(
                pendingCount, approvedToday, rejectedToday, approvedWeek, rejectedWeek,
                outboxPending, outboxInFlight, outboxFailed, recentDtos, trend));
        });

        return app;
    }
}

public static class InternalEndpoints
{
    // OutboxDrainWorker polls every 2s (Bot/Workers.cs) — a real Telegram call finishing in minutes
    // would be extraordinary, so anything still InFlight this long almost certainly means the bot
    // that claimed it crashed/restarted before acking, not that the call is merely slow.
    private static readonly TimeSpan StaleInFlightThreshold = TimeSpan.FromMinutes(2);
    private const int MaxOutboxAttempts = 5;

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

        // The bot's "/status" DM command: a user's own application can live in any tenant's DB and
        // the bot has no persistent chat->tenant mapping outside an active survey (TenantRouter is
        // forgotten once the survey completes), so this scans active tenants the same way the two
        // endpoints above do, keeping only the single most recent application found across all of them.
        g.MapGet("/applications/by-user/{telegramUserId:long}", async (
            long telegramUserId, CatalogDbContext catalog, TenantDbContextFactory factory, CancellationToken ct) =>
        {
            Domain.Application? best = null;
            long bestTenantId = 0;
            var tenants = await catalog.Tenants.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
            foreach (var tenant in tenants)
            {
                await using var db = factory.ForDatabase(tenant.DatabaseName);
                var candidate = await db.Applications.AsNoTracking()
                    .Where(a => a.TelegramUserId == telegramUserId)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (candidate is not null && (best is null || candidate.CreatedAt > best.CreatedAt))
                {
                    best = candidate;
                    bestTenantId = tenant.Id;
                }
            }
            return best is null
                ? Results.NotFound()
                : Results.Ok(new LatestApplicationStatusDto(bestTenantId, best.Status.ToString(), best.SubmittedAt));
        });

        // Outbox drain: scan active tenants, claim Pending commands, hand them to the bot. Before
        // claiming anything new, first reclaims commands stuck InFlight too long — a bot that
        // crashed/restarted between MarkInFlight and its ack would otherwise strand them forever,
        // since nothing else ever re-queries a non-Pending row.
        g.MapGet("/telegram-commands/pending", async (
            int? batch, CatalogDbContext catalog, TenantDbContextFactory factory, IClock clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            var take = batch is > 0 ? batch.Value : 32;
            var result = new List<PendingCommand>();

            var tenants = await catalog.Tenants.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
            foreach (var tenant in tenants)
            {
                await using var db = factory.ForDatabase(tenant.DatabaseName);

                var staleCutoff = now - StaleInFlightThreshold;
                var stale = await db.TelegramCommands
                    .Where(c => c.Status == TelegramCommandStatus.InFlight && c.InFlightAt < staleCutoff)
                    .ToListAsync(ct);
                if (stale.Count > 0)
                {
                    foreach (var cmd in stale) cmd.ReclaimStale(now, MaxOutboxAttempts);
                    await db.SaveChangesAsync(ct);
                }

                if (result.Count >= take) continue;

                var pending = await db.TelegramCommands
                    .Where(c => c.Status == TelegramCommandStatus.Pending)
                    .OrderBy(c => c.Id)
                    .Take(take - result.Count)
                    .ToListAsync(ct);
                if (pending.Count == 0) continue;

                foreach (var cmd in pending) cmd.MarkInFlight(now);
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

        // Catalog-level, not tenant-scoped — UI/bot text is identical across every tenant. Bot and
        // Web each bulk-load this into their own periodically-refreshed cache (see TranslationCache
        // in each project), never look it up per-message/per-render.
        g.MapGet("/translations", async (ITranslationRepository translations, CancellationToken ct) =>
        {
            var all = await translations.GetAllAsync(ct);
            return Results.Ok(all.Select(t => new TranslationDto(t.Id, t.Key, t.LanguageCode, t.Value)).ToList());
        });

        // Owner-only edit from the site's /translations page — upserts one (Key, LanguageCode) pair.
        g.MapPut("/translations", async (
            UpsertTranslationRequest body, UpsertTranslationHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new UpsertTranslationCommand(body.Key, body.LanguageCode, body.Value), ct);
            return Results.NoContent();
        });

        return app;
    }

    private static readonly Regex TenantSlugPattern = new("^[a-z0-9_-]+$", RegexOptions.Compiled);
}
