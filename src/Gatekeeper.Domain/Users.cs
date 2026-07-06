namespace Gatekeeper.Domain;

/// <summary>
/// A person, keyed by the immutable Telegram user id. Holds the latest profile plus a
/// full history of identity snapshots, so a returning applicant who renamed themselves
/// is still recognisable. Ban history attaches to this aggregate via TelegramUserId.
/// </summary>
public sealed class TelegramUser : AggregateRoot
{
    private readonly List<UserIdentitySnapshot> _history = [];

    public long TelegramUserId { get; private set; }
    public bool IsBot { get; private set; }
    public bool IsPremium { get; private set; }
    public string? Username { get; private set; }
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public string? LanguageCode { get; private set; }
    public string? Bio { get; private set; }
    public string? PhoneNumber { get; private set; }       // only if the user shares a contact
    public string? PhotoFileId { get; private set; }
    public string NameNormalized { get; private set; } = "";  // lower-cased, stripped — feeds the pg_trgm index
    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }

    public IReadOnlyCollection<UserIdentitySnapshot> History => _history.AsReadOnly();

    private TelegramUser() { }

    public static TelegramUser FirstSighting(
        long telegramUserId, bool isBot, bool isPremium, string? username, string? firstName,
        string? lastName, string? languageCode, string? bio, Func<string?, string?, string> normalize,
        DateTimeOffset now)
    {
        var user = new TelegramUser
        {
            TelegramUserId = telegramUserId,
            IsBot = isBot,
            IsPremium = isPremium,
            LanguageCode = languageCode,
            FirstSeenAt = now,
        };
        user.RecordSighting(username, firstName, lastName, bio, photoFileId: null, source: "first_seen", normalize, now);
        return user;
    }

    /// <summary>
    /// Updates the latest profile and appends a snapshot whenever any visible field changed.
    /// Returns the snapshot that represents the identity at this moment (an application links to it).
    /// </summary>
    public UserIdentitySnapshot RecordSighting(
        string? username, string? firstName, string? lastName, string? bio, string? photoFileId,
        string source, Func<string?, string?, string> normalize, DateTimeOffset now)
    {
        var changed = username != Username || firstName != FirstName || lastName != LastName
                      || bio != Bio || photoFileId != PhotoFileId;

        Username = username;
        FirstName = firstName;
        LastName = lastName;
        Bio = bio;
        if (photoFileId is not null) PhotoFileId = photoFileId;
        NameNormalized = normalize(firstName, lastName);
        LastSeenAt = now;

        if (changed || _history.Count == 0)
        {
            var snapshot = UserIdentitySnapshot.Capture(Id, username, firstName, lastName, bio, PhotoFileId, source, now);
            _history.Add(snapshot);
            return snapshot;
        }

        return _history[^1];
    }

    public void SetPhoneNumber(string phoneNumber) => PhoneNumber = phoneNumber;
}

/// <summary>An immutable photograph of how a user presented at a point in time.</summary>
public sealed class UserIdentitySnapshot : Entity
{
    public long TelegramUserId { get; private set; }   // FK to TelegramUser.Id (surrogate)
    public string? Username { get; private set; }
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public string? Bio { get; private set; }
    public string? PhotoFileId { get; private set; }
    public string Source { get; private set; } = "";
    public DateTimeOffset ObservedAt { get; private set; }

    private UserIdentitySnapshot() { }

    public static UserIdentitySnapshot Capture(
        long userKey, string? username, string? firstName, string? lastName,
        string? bio, string? photoFileId, string source, DateTimeOffset now) => new()
    {
        TelegramUserId = userKey,
        Username = username,
        FirstName = firstName,
        LastName = lastName,
        Bio = bio,
        PhotoFileId = photoFileId,
        Source = source,
        ObservedAt = now,
    };
}
