namespace Gatekeeper.Domain;

/// <summary>
/// A single page view against the admin site — who, when, from where, what. Distinct from
/// ModerationAction, which records WHAT an admin decided, not that they merely looked at a page.
/// Append-only; no domain events, no aggregate behavior beyond recording.
/// </summary>
public sealed class SiteVisit : Entity
{
    public long? UserId { get; private set; }         // Telegram user id (ClaimTypes.NameIdentifier) — null if unauthenticated (e.g. /login itself)
    public string? UserName { get; private set; }
    public string SessionId { get; private set; } = default!;  // groups multiple page views into one visit, via a long-lived cookie
    public string? IpAddress { get; private set; }
    public string Path { get; private set; } = default!;
    public string Method { get; private set; } = default!;
    public int StatusCode { get; private set; }
    public long DurationMs { get; private set; }
    public string? UserAgent { get; private set; }
    public string? Referrer { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private SiteVisit() { }

    public static SiteVisit Record(
        long? userId, string? userName, string sessionId, string? ipAddress, string path, string method,
        int statusCode, long durationMs, string? userAgent, string? referrer, DateTimeOffset now) => new()
    {
        UserId = userId,
        UserName = userName,
        SessionId = sessionId,
        IpAddress = ipAddress,
        Path = path,
        Method = method,
        StatusCode = statusCode,
        DurationMs = durationMs,
        UserAgent = userAgent,
        Referrer = referrer,
        CreatedAt = now,
    };
}
