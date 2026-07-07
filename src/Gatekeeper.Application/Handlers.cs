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
 
        return first is null
            ? new FirstQuestion(null, "Welcome! There are no questions to answer yet.", nameof(QuestionType.Text), Completed: true)
            : new FirstQuestion(first.Id, first.PromptText, first.Type.ToString(), Completed: false);
    }
 
    private static string Normalize(string? first, string? last) =>
        $"{first} {last}".Trim().ToLowerInvariant();
}
 
// --- Submit a survey answer -----------------------------------------------------------------
 
public sealed record SubmitAnswerCommand(long TelegramUserId, string? Text);
 
public sealed record NextStep(long? QuestionId, string? Prompt, string? Type, bool Completed);
 
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
 
        // The applicant's first private message starts the survey.
        if (application.Status == ApplicationStatus.SurveyOffered)
        {
            var firstQ = await questions.GetFirstActiveAsync(ct)
                ?? throw new InvalidOperationException("No active questions configured.");
            application.StartSurvey(firstQ.Id, now);
        }
 
        var currentId = application.Session!.CurrentQuestionId
            ?? throw new InvalidOperationException("Survey has no current question.");
        var current = await questions.GetByIdAsync(currentId, ct)
            ?? throw new InvalidOperationException("Current question not found.");
 
        var answer = Answer.Create(current.Id, current.PromptText, current.Type, current.Position,
            cmd.Text, optionsJson: null, now);
        var next = await questions.GetNextActiveAsync(current.Position, ct);
        application.AddAnswer(answer, next?.Id, now);
 
        if (next is null)
        {
            // Completed → notify the admin group via the outbox (durable, single executor).
            var adminChatId = await directory.GetAdminChatIdAsync(tenant.TenantId, ct)
                ?? throw new InvalidOperationException("Admin chat is not configured for this tenant.");
 
            var user = await users.GetByTelegramIdAsync(cmd.TelegramUserId, ct);
            var header = AdminCardText.BuildHeader(
                application.Id, user?.Username, user?.FirstName, user?.LastName, application.SubmittedAt);

            var payload = new TelegramCommandPayload(
                ChatId: adminChatId,
                Text: header,
                PhotoFileId: user?.PhotoFileId,
                Buttons: [
                    new TelegramButton("✅ Approve", $"appr:{application.Id}"),
                    new TelegramButton("❌ Reject", $"rej:{application.Id}"),
                ]);
            queue.Enqueue(TelegramCommand.Enqueue(TelegramCommandType.SendAdminCard,
                JsonSerializer.Serialize(payload), application.Id, cmd.TelegramUserId, now));
        }
 
        await unitOfWork.SaveChangesAsync(ct);
 
        return next is null
            ? new NextStep(null, null, null, Completed: true)
            : new NextStep(next.Id, next.PromptText, next.Type.ToString(), Completed: false);
    }
}