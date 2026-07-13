using System.Text.Json;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;

namespace Gatekeeper.Application;

// --- Create application from a join request -------------------------------------------------
 
public sealed record CreateApplicationCommand(
    long ChatId, long TelegramUserId, bool IsBot, bool IsPremium,
    string? Username, string? FirstName, string? LastName, string? LanguageCode,
    long? UserChatId, string? InviteLink, string? Bio, string? PhotoFileId = null);
 
/// <summary>The first question (or completion) returned to the bot to DM the applicant.</summary>
public sealed record FirstQuestion(long? QuestionId, string Prompt, string Type, bool Completed);
 
public sealed class CreateApplicationHandler(
    IUserRepository users,
    IApplicationRepository applications,
    IQuestionRepository questions,
    ITranslationRepository translations,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<FirstQuestion> HandleAsync(CreateApplicationCommand cmd, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
 
        var user = await users.GetByTelegramIdAsync(cmd.TelegramUserId, ct);
        if (user is null)
        {
            user = TelegramUser.FirstSighting(cmd.TelegramUserId, cmd.IsBot, cmd.IsPremium,
                cmd.Username, cmd.FirstName, cmd.LastName, cmd.LanguageCode, cmd.Bio, cmd.PhotoFileId, Normalize, now);
            await users.AddAsync(user, ct);
        }
        var snapshot = user.RecordSighting(cmd.Username, cmd.FirstName, cmd.LastName, cmd.Bio,
            cmd.PhotoFileId, source: "join_request", Normalize, now);
 
        var application = Domain.Application.FromJoinRequest(cmd.TelegramUserId, cmd.ChatId, cmd.UserChatId, cmd.InviteLink, now);
        await applications.AddAsync(application, ct);
        application.MarkSurveyOffered();
 
        var first = await questions.GetFirstActiveAsync(ct);
 
        await unitOfWork.SaveChangesAsync(ct);     // assigns ids
        application.LinkIdentitySnapshot(snapshot.Id);
        await unitOfWork.SaveChangesAsync(ct);
 
        if (first is not null)
            return new FirstQuestion(first.Id, first.PromptText, first.Type.ToString(), Completed: false);

        var lang = Lang.Resolve(cmd.LanguageCode);
        var noQuestions = await translations.GetValueAsync("bot.no_questions", lang, ct);
        return new FirstQuestion(null, noQuestions, nameof(QuestionType.Text), Completed: true);
    }
 
    private static string Normalize(string? first, string? last) =>
        $"{first} {last}".Trim().ToLowerInvariant();
}
 
// --- Submit a survey answer -----------------------------------------------------------------
 
public sealed record SubmitAnswerCommand(long TelegramUserId, string? Text, IReadOnlyList<int>? SelectedOptionIndexes = null);

public sealed record NextStep(
    long? QuestionId, string? Prompt, string? Type, bool Completed,
    string? LanguageCode = null, IReadOnlyList<string>? Options = null);

public sealed class SubmitAnswerHandler(
    IApplicationRepository applications,
    IQuestionRepository questions,
    IUserRepository users,
    ITelegramCommandQueue queue,
    ITenantDirectory directory,
    ITenantContext tenant,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<NextStep> HandleAsync(SubmitAnswerCommand cmd, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        var application = await applications.GetActiveForUserAsync(cmd.TelegramUserId, ct)
            ?? throw new InvalidOperationException("No active application for this user.");

        // The survey must be explicitly started via StartSurveyHandler (the language-picker's
        // "lang:go"/"lang:ru"/"lang:en" callbacks) before any answer can be recorded — a stray
        // message sent instead of tapping a button no longer silently starts it and consumes that
        // message as the answer to question #1 (see docs/changelog.md 2026-07-11).
        if (application.Status == ApplicationStatus.SurveyOffered)
            throw new SurveyNotStartedException("Survey has not been started yet — use the language picker first.");

        var currentId = application.Session!.CurrentQuestionId
            ?? throw new InvalidOperationException("Survey has no current question.");
        var current = await questions.GetByIdAsync(currentId, ct)
            ?? throw new InvalidOperationException("Current question not found.");

        var user = await users.GetByTelegramIdAsync(cmd.TelegramUserId, ct);
        var lang = Lang.Resolve(user?.LanguageCode);

        string? answerText;
        string? optionsJson;
        if (current.Type is QuestionType.SingleChoice or QuestionType.MultiChoice)
        {
            var options = current.OptionsFor(lang) ?? [];
            if (cmd.SelectedOptionIndexes is not { Count: > 0 })
            {
                // Stray free text (or an empty submit) while a button-only question is pending —
                // re-prompt the same question instead of silently recording it as the answer.
                return new NextStep(current.Id, current.PromptTextFor(lang), current.Type.ToString(), Completed: false,
                    user?.LanguageCode, options);
            }

            var labels = cmd.SelectedOptionIndexes.Where(i => i >= 0 && i < options.Count).Select(i => options[i]).ToList();
            answerText = string.Join(", ", labels);
            optionsJson = ChoiceOptions.SerializeSelectedIndices(cmd.SelectedOptionIndexes);
        }
        else
        {
            answerText = cmd.Text;
            optionsJson = null;
        }

        // Freezes whichever language variant was actually shown to the applicant — same job as
        // every other snapshot field here (recording history even if the live Question changes
        // later), now also true of its language.
        var answer = Answer.Create(current.Id, current.PromptTextFor(lang), current.Type, current.Position,
            answerText, optionsJson, now);
        var next = await questions.GetNextActiveAsync(current.Position, ct);
        application.AddAnswer(answer, next?.Id, now);

        if (next is null)
        {
            // Completed → notify the admin group via the outbox (durable, single executor).
            var adminChatId = await directory.GetAdminChatIdAsync(tenant.TenantId, ct)
                ?? throw new InvalidOperationException("Admin chat is not configured for this tenant.");

            var header = AdminCardText.BuildHeader(application.Id, user?.Username, user?.FirstName, user?.LastName, user?.Bio);

            var payload = new TelegramCommandPayload(
                ChatId: adminChatId,
                Text: header,
                PhotoFileId: user?.PhotoFileId,
                Buttons: [
                    new TelegramButton("✅ Approve", $"appr:{application.Id}"),
                    new TelegramButton("❌ Reject", $"rej:{application.Id}"),
                    new TelegramButton("📄 Показать ответы", $"ans:{application.Id}"),
                ]);
            queue.Enqueue(TelegramCommand.Enqueue(TelegramCommandType.SendAdminCard,
                JsonSerializer.Serialize(payload), application.Id, cmd.TelegramUserId, now));
        }

        await unitOfWork.SaveChangesAsync(ct);

        return next is null
            ? new NextStep(null, null, null, Completed: true, user?.LanguageCode)
            : new NextStep(next.Id, next.PromptTextFor(lang), next.Type.ToString(), Completed: false,
                user?.LanguageCode, next.OptionsFor(lang));
    }
}

// --- Explicitly start the survey (language-picker "lang:go"/"lang:ru"/"lang:en") -------------

public sealed record StartSurveyCommand(long TelegramUserId);

public sealed class StartSurveyHandler(
    IApplicationRepository applications,
    IQuestionRepository questions,
    IUserRepository users,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<NextStep> HandleAsync(StartSurveyCommand cmd, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        var application = await applications.GetActiveForUserAsync(cmd.TelegramUserId, ct)
            ?? throw new InvalidOperationException("No active application for this user.");
        var user = await users.GetByTelegramIdAsync(cmd.TelegramUserId, ct);
        var lang = Lang.Resolve(user?.LanguageCode);

        if (application.Status == ApplicationStatus.SurveyOffered)
        {
            var firstQ = await questions.GetFirstActiveAsync(ct)
                ?? throw new InvalidOperationException("No active questions configured.");
            application.StartSurvey(firstQ.Id, now);
            await unitOfWork.SaveChangesAsync(ct);
            return new NextStep(firstQ.Id, firstQ.PromptTextFor(lang), firstQ.Type.ToString(), Completed: false,
                user?.LanguageCode, firstQ.OptionsFor(lang));
        }

        // Idempotent re-tap (double-tap "Продолжить", or tapping again after already progressing
        // via a normal answer) — re-show wherever they currently are rather than erroring via
        // StartSurvey's own EnsureStatus guard.
        if (application.Status == ApplicationStatus.InSurvey && application.Session?.CurrentQuestionId is { } currentId)
        {
            var current = await questions.GetByIdAsync(currentId, ct);
            if (current is not null)
                return new NextStep(current.Id, current.PromptTextFor(lang), current.Type.ToString(), Completed: false,
                    user?.LanguageCode, current.OptionsFor(lang));
        }

        // Degenerate fallback (InSurvey but no current question, e.g. it was deleted mid-survey) —
        // GetActiveForUserAsync only ever returns SurveyOffered/InSurvey applications in the first
        // place, so a stale tap after the survey is already decided already fails earlier, at the
        // "No active application" guard above, consistent with SubmitAnswerHandler's own behavior.
        return new NextStep(null, null, null, Completed: true, user?.LanguageCode);
    }
}