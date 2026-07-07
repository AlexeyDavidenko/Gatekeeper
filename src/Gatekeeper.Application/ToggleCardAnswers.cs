using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;

namespace Gatekeeper.Application;

using System.Text.Json;

public sealed record ToggleCardAnswersCommand(long ApplicationId, bool Show);

/// <summary>
/// Show/hide-answers button on the moderation card. Purely a display toggle — the target state
/// comes straight from which button was pressed ("ans"/"hideans"), so nothing needs to be
/// persisted to know it. Rebuilds the full card text (header + optional answers + optional
/// verdict) and re-sends it as an EditMessage outbox command, same executor path as everything else.
/// </summary>
public sealed class ToggleCardAnswersHandler(
    IApplicationRepository applications,
    IModerationRepository moderation,
    ITelegramCommandQueue queue,
    ITenantDirectory directory,
    ITenantContext tenant,
    IUserRepository users,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task HandleAsync(ToggleCardAnswersCommand cmd, CancellationToken ct = default)
    {
        var application = await applications.GetAsync(cmd.ApplicationId, ct)
            ?? throw new InvalidOperationException($"Application {cmd.ApplicationId} not found.");

        if (application.AdminCardMessageId is not { } cardMessageId)
            return;   // card hasn't been sent yet — nothing to edit

        var adminChatId = await directory.GetAdminChatIdAsync(tenant.TenantId, ct);
        if (adminChatId is not { } chatId)
            return;

        var user = await users.GetByTelegramIdAsync(application.TelegramUserId, ct);
        var text = AdminCardText.BuildHeader(application.Id, user?.Username, user?.FirstName, user?.LastName, user?.Bio);

        if (cmd.Show)
            text += AdminCardText.BuildAnswersBlock(application.Answers);

        var decided = application.Status is ApplicationStatus.Approved or ApplicationStatus.Rejected;
        List<TelegramButton> buttons = [ToggleButton(application.Id, cmd.Show)];
        if (decided)
        {
            var decision = await moderation.GetLatestDecisionAsync(application.Id, ct);
            if (decision is not null)
                text += AdminCardText.BuildVerdict(decision.Action == ModerationActionType.Approve,
                    decision.PerformedByName ?? decision.PerformedByUserId.ToString());
        }
        else
        {
            buttons.Insert(0, new TelegramButton("❌ Reject", $"rej:{application.Id}"));
            buttons.Insert(0, new TelegramButton("✅ Approve", $"appr:{application.Id}"));
        }

        queue.Enqueue(TelegramCommand.Enqueue(
            TelegramCommandType.EditMessage,
            JsonSerializer.Serialize(new TelegramCommandPayload(
                ChatId: chatId, MessageId: cardMessageId, Text: text, IsPhotoCaption: application.AdminCardHasPhoto,
                Buttons: buttons)),
            application.Id, application.TelegramUserId, clock.UtcNow));

        await unitOfWork.SaveChangesAsync(ct);
    }

    private static TelegramButton ToggleButton(long applicationId, bool currentlyShown) =>
        currentlyShown
            ? new TelegramButton("🙈 Скрыть ответы", $"hideans:{applicationId}")
            : new TelegramButton("📄 Показать ответы", $"ans:{applicationId}");
}
