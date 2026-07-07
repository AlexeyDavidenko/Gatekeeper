using Gatekeeper.Domain;

namespace Gatekeeper.Application.Applications;

using Gatekeeper.Application;
using Gatekeeper.Domain.Outbox;
using System.Text.Json;

public sealed record DecideApplicationCommand(
    long ApplicationId,
    bool Approve,
    string? Reason,
    long ActingUserId,
    string? ActingUserName,
    ActionSource Source,
    uint? ExpectedRowVersion);

/// <summary>
/// Approves or rejects a join application. Same handler serves the admin-group buttons and the
/// website — both write to one outbox, so the bot is the only executor and the first to reach
/// Telegram wins. Optimistic concurrency (xmin) blocks a second decision from clobbering the first.
/// </summary>
public sealed class DecideApplicationHandler(
    IApplicationRepository applications,
    IModerationRepository moderation,
    ITelegramCommandQueue queue,
    ITenantDirectory directory,
    ITenantContext tenant,
    IUserRepository users,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task HandleAsync(DecideApplicationCommand cmd, CancellationToken ct = default)
    {
        var application = await applications.GetAsync(cmd.ApplicationId, ct)
            ?? throw new InvalidOperationException($"Application {cmd.ApplicationId} not found.");

        // Fast pre-check; the real guarantee is the xmin token at SaveChanges.
        if (application.Status is ApplicationStatus.Approved or ApplicationStatus.Rejected)
            throw new ConcurrencyConflictException($"Application is already {application.Status}.");

        if (cmd.ExpectedRowVersion is { } expected && application.RowVersion != expected)
            throw new ConcurrencyConflictException("Application was modified by someone else.");

        var now = clock.UtcNow;

        if (cmd.Approve) application.Approve(now);
        else application.Reject(now);

        // 1) Unified audit log entry.
        var audit = ModerationAction.ForDecision(
            application.TelegramUserId, application.Id, application.MainChatId,
            cmd.Approve ? ModerationActionType.Approve : ModerationActionType.Reject,
            cmd.Reason, cmd.ActingUserId, cmd.ActingUserName, cmd.Source, now);
        await moderation.AddAsync(audit, ct);

        // 2) Side effects go through the outbox — never call Telegram inline.
        queue.Enqueue(TelegramCommand.Enqueue(
            cmd.Approve ? TelegramCommandType.ApproveJoinRequest : TelegramCommandType.DeclineJoinRequest,
            JsonSerializer.Serialize(new TelegramCommandPayload(ChatId: application.MainChatId, UserId: application.TelegramUserId)),
            application.Id, application.TelegramUserId, now));

        queue.Enqueue(TelegramCommand.Enqueue(
            TelegramCommandType.SendMessage,
            JsonSerializer.Serialize(new TelegramCommandPayload(
                ChatId: application.TelegramUserId,
                Text: cmd.Approve ? "You're approved — welcome!" : "Your application was declined.")),
            application.Id, application.TelegramUserId, now));

        // 3) Reflect the decision on the moderation card, if the outbox drain already reported its
        //    message_id back — best-effort, skipped silently if it hasn't (yet).
        if (application.AdminCardMessageId is { } cardMessageId)
        {
            var adminChatId = await directory.GetAdminChatIdAsync(tenant.TenantId, ct);
            if (adminChatId is { } chatId)
            {
                var user = await users.GetByTelegramIdAsync(application.TelegramUserId, ct);
                var header = AdminCardText.BuildHeader(application.Id, user?.Username, user?.FirstName, user?.LastName, user?.Bio);
                var by = cmd.ActingUserName ?? cmd.ActingUserId.ToString();
                var text = header + AdminCardText.BuildVerdict(cmd.Approve, by);

                // Decision collapses the answers block back down — approve/reject buttons are gone
                // (nothing left to decide), only the show-answers toggle survives.
                queue.Enqueue(TelegramCommand.Enqueue(
                    TelegramCommandType.EditMessage,
                    JsonSerializer.Serialize(new TelegramCommandPayload(
                        ChatId: chatId, MessageId: cardMessageId, Text: text, IsPhotoCaption: application.AdminCardHasPhoto,
                        Buttons: [new TelegramButton("📄 Показать ответы", $"ans:{application.Id}")])),
                    application.Id, application.TelegramUserId, now));
            }
        }

        // 4) Commit atomically. A concurrent decision bumps xmin → DbUpdateConcurrencyException,
        //    surfaced as ConcurrencyConflictException by the repository/SaveChanges wrapper.
        await unitOfWork.SaveChangesAsync(ct);
    }
}
