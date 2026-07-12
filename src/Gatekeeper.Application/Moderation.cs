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
    // Returns the new ModerationAction's id — callers (e.g. the evidence-upload flow) need it to
    // attach a screenshot to the action that was just created.
    public async Task<long> HandleAsync(ModerateUserCommand cmd, CancellationToken ct = default)
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
        return action.Id;
    }
}

public sealed record AttachEvidenceCommand(long ModerationActionId, Stream Content, string ContentType, long ActingUserId);

/// <summary>Deliberately independent of ModerateUserHandler — creating an action and attaching
/// evidence to it are separate operations, so evidence can be retried or attached later without
/// re-doing the action itself, and (not wired up yet) could equally attach to an approve/reject
/// decision in the future without any changes here.</summary>
public sealed class AttachEvidenceHandler(
    IModerationRepository moderation, IEvidenceStorage storage, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task HandleAsync(AttachEvidenceCommand cmd, CancellationToken ct = default)
    {
        var action = await moderation.GetAsync(cmd.ModerationActionId, ct)
            ?? throw new InvalidOperationException($"ModerationAction {cmd.ModerationActionId} not found.");

        var (storagePath, hash, sizeBytes) = await storage.SaveAsync(cmd.Content, cmd.ContentType, ct);
        action.AttachEvidence(storagePath, hash, cmd.ContentType, sizeBytes, cmd.ActingUserId, clock.UtcNow);

        await unitOfWork.SaveChangesAsync(ct);
    }
}

public sealed record ArchiveModerationActionsCommand(DateTimeOffset OlderThan);

/// <summary>Owner-triggered bulk cleanup — no automatic/scheduled purge exists, this is the only
/// way old ModerationAction rows ever get archived. Reversible per-row via UnarchiveModerationActionHandler.</summary>
public sealed class ArchiveModerationActionsHandler(IModerationRepository moderation, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task<int> HandleAsync(ArchiveModerationActionsCommand cmd, CancellationToken ct = default)
    {
        var stale = await moderation.GetActiveOlderThanAsync(cmd.OlderThan, ct);
        var now = clock.UtcNow;
        foreach (var action in stale) action.Archive(now);
        await unitOfWork.SaveChangesAsync(ct);
        return stale.Count;
    }
}

public sealed record UnarchiveModerationActionCommand(long ModerationActionId);

public sealed class UnarchiveModerationActionHandler(IModerationRepository moderation, IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(UnarchiveModerationActionCommand cmd, CancellationToken ct = default)
    {
        var action = await moderation.GetAsync(cmd.ModerationActionId, ct)
            ?? throw new InvalidOperationException($"ModerationAction {cmd.ModerationActionId} not found.");
        action.Unarchive();
        await unitOfWork.SaveChangesAsync(ct);
    }
}
