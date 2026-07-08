namespace Gatekeeper.Application;

using Gatekeeper.Domain;

public sealed record RecordSiteVisitCommand(
    long? UserId, string? UserName, string SessionId, string? IpAddress, string Path, string Method,
    int StatusCode, long DurationMs, string? UserAgent, string? Referrer);

/// <summary>Writes one page-view record. Called from Web's fire-and-forget background flush
/// worker (see Gatekeeper.Web/SiteVisitLogging.cs) — never on the request-serving path itself.</summary>
public sealed class RecordSiteVisitHandler(ISiteVisitRepository visits, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task HandleAsync(RecordSiteVisitCommand cmd, CancellationToken ct = default)
    {
        var visit = SiteVisit.Record(
            cmd.UserId, cmd.UserName, cmd.SessionId, cmd.IpAddress, cmd.Path, cmd.Method,
            cmd.StatusCode, cmd.DurationMs, cmd.UserAgent, cmd.Referrer, clock.UtcNow);
        await visits.AddAsync(visit, ct);
        await unitOfWork.SaveChangesAsync(ct);
    }
}
