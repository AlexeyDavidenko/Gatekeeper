namespace Gatekeeper.Domain.Outbox;

/// <summary>
/// A durable, persisted intent to perform a Telegram side effect. Written in the same
/// transaction as the state change that triggered it (transactional outbox). The bot is the
/// single drainer/executor, which gives idempotency and enforces the "Telegram wins" rule.
/// </summary>
public sealed class TelegramCommand : AggregateRoot
{
    public TelegramCommandType Type { get; private set; }
    public string PayloadJson { get; private set; } = default!;
    public TelegramCommandStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public long? ApplicationId { get; private set; }
    public long? TelegramUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? InFlightAt { get; private set; }    // when the current attempt was claimed
    public DateTimeOffset? CompletedAt { get; private set; }
    public uint RowVersion { get; private set; }   // xmin — prevents two drainers grabbing the same command

    private TelegramCommand() { }

    public static TelegramCommand Enqueue(
        TelegramCommandType type, string payloadJson, long? applicationId, long? telegramUserId, DateTimeOffset now) => new()
    {
        Type = type,
        PayloadJson = payloadJson,
        Status = TelegramCommandStatus.Pending,
        ApplicationId = applicationId,
        TelegramUserId = telegramUserId,
        CreatedAt = now,
    };

    public void MarkInFlight(DateTimeOffset now)
    {
        Status = TelegramCommandStatus.InFlight;
        InFlightAt = now;
        Attempts++;
    }

    public void MarkSucceeded(DateTimeOffset now)
    {
        Status = TelegramCommandStatus.Succeeded;
        CompletedAt = now;
        LastError = null;
    }

    public void MarkFailed(string error, DateTimeOffset now)
    {
        // Treat a "request already processed" style failure as terminal-success upstream if needed;
        // here we record it and let the drainer's retry policy decide.
        Status = TelegramCommandStatus.Failed;
        LastError = error;
        CompletedAt = now;
    }

    /// <summary>True once a claimed command has sat InFlight longer than staleAfter without an ack —
    /// the bot that claimed it almost certainly crashed/restarted mid-send, not that Telegram is slow.</summary>
    public bool IsStale(DateTimeOffset now, TimeSpan staleAfter) =>
        Status == TelegramCommandStatus.InFlight && InFlightAt is { } claimedAt && now - claimedAt > staleAfter;

    /// <summary>Recovers a stale InFlight command: retried (back to Pending) while attempts remain,
    /// otherwise given up as Failed so a permanently-broken command doesn't retry forever.</summary>
    public void ReclaimStale(DateTimeOffset now, int maxAttempts)
    {
        if (Attempts >= maxAttempts)
        {
            MarkFailed("Exceeded max retry attempts after getting stuck in-flight.", now);
            return;
        }

        Status = TelegramCommandStatus.Pending;
        InFlightAt = null;
    }
}

/// <summary>Structured payload stored in <see cref="TelegramCommand.PayloadJson"/>. Shared by producer and drainer.</summary>
public sealed record TelegramCommandPayload(
    long? ChatId = null,
    long? UserId = null,
    long? MessageId = null,
    string? Text = null,
    IReadOnlyList<TelegramButton>? Buttons = null,
    string? PhotoFileId = null,
    bool IsPhotoCaption = false);

public sealed record TelegramButton(string Text, string CallbackData);
