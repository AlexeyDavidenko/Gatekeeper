using System.Text.Json;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;

namespace Gatekeeper.Application;

public sealed record ModerateUserCommand(
    long TelegramUserId, long ChatId, ModerationActionType Action, string? Reason, string? Notes,
    long ActingUserId, string? ActingUserName, ActionSource Source, DateTimeOffset? ExpiresAt);

public sealed class ModerateUserHandler(
    IModerationRepository moderation,
    ITelegramCommandQueue queue,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task HandleAsync(ModerateUserCommand cmd, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        var action = ModerationAction.ForMember(
            cmd.TelegramUserId, cmd.ChatId, cmd.Action, cmd.Reason, cmd.Notes,
            cmd.ActingUserId, cmd.ActingUserName, cmd.Source, cmd.ExpiresAt, now);
        await moderation.AddAsync(action, ct);

        // Map the action to a Telegram side effect where one applies (warn/approve/reject don't).
        TelegramCommandType? type = cmd.Action switch
        {
            ModerationActionType.Ban or ModerationActionType.Kick => TelegramCommandType.BanUser,
            ModerationActionType.Unban => TelegramCommandType.UnbanUser,
            ModerationActionType.Mute or ModerationActionType.Unmute => TelegramCommandType.RestrictUser,
            _ => null,
        };
        if (type is { } commandType)
        {
            var payload = new TelegramCommandPayload(ChatId: cmd.ChatId, UserId: cmd.TelegramUserId);
            queue.Enqueue(TelegramCommand.Enqueue(commandType, JsonSerializer.Serialize(payload),
                applicationId: null, telegramUserId: cmd.TelegramUserId, now));
        }

        await unitOfWork.SaveChangesAsync(ct);
    }
}
