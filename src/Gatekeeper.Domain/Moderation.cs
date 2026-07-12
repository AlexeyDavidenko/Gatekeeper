namespace Gatekeeper.Domain;

/// <summary>
/// Append-only audit record for every admin action: approve/reject of an application AND
/// ban/mute/etc on a member. Keyed to the user (stable id) so a returning applicant's ban
/// history surfaces immediately. ApplicationId is optional — moderation often happens after joining.
/// </summary>
public sealed class ModerationAction : AggregateRoot
{
    private readonly List<ModerationAttachment> _attachments = [];

    public long TelegramUserId { get; private set; }
    public long? ApplicationId { get; private set; }
    public long ChatId { get; private set; }
    public ModerationActionType Action { get; private set; }
    public string? Reason { get; private set; }       // short
    public string? Notes { get; private set; }         // long free text — pasted logs, context
    public long PerformedByUserId { get; private set; }
    public string? PerformedByName { get; private set; }
    public ActionSource Source { get; private set; }
    public string? TelegramResult { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }   // temp mute/ban
    public DateTimeOffset CreatedAt { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    public IReadOnlyCollection<ModerationAttachment> Attachments => _attachments.AsReadOnly();

    private ModerationAction() { }

    public static ModerationAction ForDecision(
        long telegramUserId, long applicationId, long chatId, ModerationActionType action, string? reason,
        long performedByUserId, string? performedByName, ActionSource source, DateTimeOffset now) => new()
    {
        TelegramUserId = telegramUserId,
        ApplicationId = applicationId,
        ChatId = chatId,
        Action = action,
        Reason = reason,
        PerformedByUserId = performedByUserId,
        PerformedByName = performedByName,
        Source = source,
        CreatedAt = now,
    };

    public static ModerationAction ForMember(
        long telegramUserId, long chatId, ModerationActionType action, string? reason, string? notes,
        long performedByUserId, string? performedByName, ActionSource source,
        DateTimeOffset? expiresAt, DateTimeOffset now) => new()
    {
        TelegramUserId = telegramUserId,
        ChatId = chatId,
        Action = action,
        Reason = reason,
        Notes = notes,
        PerformedByUserId = performedByUserId,
        PerformedByName = performedByName,
        Source = source,
        ExpiresAt = expiresAt,
        CreatedAt = now,
    };

    public void SetTelegramResult(string result) => TelegramResult = result;

    // Reversible — an Owner archiving old log entries is a cleanup action, not a correction of
    // the record itself, so undoing it must be possible (mirrors Question.Activate/Deactivate).
    public void Archive(DateTimeOffset now)
    {
        IsArchived = true;
        ArchivedAt = now;
    }

    public void Unarchive()
    {
        IsArchived = false;
        ArchivedAt = null;
    }

    public ModerationAttachment AttachEvidence(
        string storagePath, string contentHash, string contentType, long sizeBytes, long uploadedBy, DateTimeOffset now)
    {
        var attachment = ModerationAttachment.Create(Id, storagePath, contentHash, contentType, sizeBytes, uploadedBy, now);
        _attachments.Add(attachment);
        return attachment;
    }
}

/// <summary>
/// Metadata for an evidence file. The blob itself lives on a volume (or MinIO), never in Postgres.
/// ContentHash (SHA-256) gives integrity and dedup.
/// </summary>
public sealed class ModerationAttachment : Entity
{
    public long ModerationActionId { get; private set; }
    public string StoragePath { get; private set; } = default!;
    public string ContentHash { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public long SizeBytes { get; private set; }
    public long UploadedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private ModerationAttachment() { }

    public static ModerationAttachment Create(
        long moderationActionId, string storagePath, string contentHash, string contentType,
        long sizeBytes, long uploadedBy, DateTimeOffset now) => new()
    {
        ModerationActionId = moderationActionId,
        StoragePath = storagePath,
        ContentHash = contentHash,
        ContentType = contentType,
        SizeBytes = sizeBytes,
        UploadedBy = uploadedBy,
        CreatedAt = now,
    };
}
