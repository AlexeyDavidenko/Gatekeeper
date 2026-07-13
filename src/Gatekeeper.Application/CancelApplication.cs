using Gatekeeper.Domain;

namespace Gatekeeper.Application.Applications;

using Gatekeeper.Application;
using Gatekeeper.Domain.Outbox;
using System.Text.Json;

public sealed record CancelApplicationCommand(long ApplicationId, long ActingUserId, string? ActingUserName);

/// <summary>
/// Admin-driven mid-survey cancel — for when a join request is declined through Telegram's own
/// native UI (which the bot is never notified about, a Bot API limitation, not something this code
/// can detect) or an admin otherwise wants to stop an applicant before they reach AwaitingReview.
/// Unlike DecideApplicationHandler, there's no admin card to edit yet (only sent once the survey
/// completes) and no competing-decision race to guard against, so this is simpler.
/// </summary>
public sealed class CancelApplicationHandler(
    IApplicationRepository applications,
    IModerationRepository moderation,
    ITelegramCommandQueue queue,
    IUserRepository users,
    ITranslationRepository translations,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task HandleAsync(CancelApplicationCommand cmd, CancellationToken ct = default)
    {
        var application = await applications.GetAsync(cmd.ApplicationId, ct)
            ?? throw new InvalidOperationException($"Application {cmd.ApplicationId} not found.");

        var now = clock.UtcNow;
        var user = await users.GetByTelegramIdAsync(application.TelegramUserId, ct);

        application.Cancel();

        // Audit log reuses Reject — same user-facing outcome (they're told no), just reached via a
        // different path than the normal AwaitingReview decision flow.
        var audit = ModerationAction.ForDecision(
            application.TelegramUserId, application.Id, application.MainChatId, ModerationActionType.Reject,
            reason: "Cancelled mid-survey", cmd.ActingUserId, cmd.ActingUserName, ActionSource.TelegramGroup, now);
        await moderation.AddAsync(audit, ct);

        queue.Enqueue(TelegramCommand.Enqueue(
            TelegramCommandType.DeclineJoinRequest,
            JsonSerializer.Serialize(new TelegramCommandPayload(ChatId: application.MainChatId, UserId: application.TelegramUserId)),
            application.Id, application.TelegramUserId, now));

        var lang = Lang.Resolve(user?.LanguageCode);
        var verdictDm = await translations.GetValueAsync("bot.decision.rejected", lang, ct);
        queue.Enqueue(TelegramCommand.Enqueue(
            TelegramCommandType.SendMessage,
            JsonSerializer.Serialize(new TelegramCommandPayload(ChatId: application.TelegramUserId, Text: verdictDm)),
            application.Id, application.TelegramUserId, now));

        await unitOfWork.SaveChangesAsync(ct);
    }
}
