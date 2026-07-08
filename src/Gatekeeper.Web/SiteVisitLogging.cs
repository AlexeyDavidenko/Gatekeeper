namespace Gatekeeper.Web;

using System.Diagnostics;
using System.Security.Claims;
using System.Threading.Channels;
using Gatekeeper.Contracts;
using Gatekeeper.Web.Services;

/// <summary>
/// In-memory hand-off from the request pipeline to a background worker — logging a visit must
/// never add latency to (or ever fail) an actual page load. Bounded + drop-oldest: losing a few
/// visit records under sustained overload is fine, blocking real traffic is not.
/// </summary>
public sealed class SiteVisitQueue
{
    private readonly Channel<RecordSiteVisitRequest> _channel =
        Channel.CreateBounded<RecordSiteVisitRequest>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public void Enqueue(RecordSiteVisitRequest visit) => _channel.Writer.TryWrite(visit);

    public ChannelReader<RecordSiteVisitRequest> Reader => _channel.Reader;
}

/// <summary>Drains the queue and forwards each visit to the Api. Best-effort — a failed write here
/// (Api briefly down, etc.) just drops that one record, never crashes or retries into a backlog.</summary>
public sealed class SiteVisitFlushWorker(
    SiteVisitQueue queue, AdminApiClient api, ILogger<SiteVisitFlushWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var visit in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await api.RecordVisitAsync(visit, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Failed to record a site visit (dropped).");
            }
        }
    }
}

/// <summary>
/// Captures one page view per request (method, path, status, duration, who, IP, UA, referrer) and
/// hands it to <see cref="SiteVisitQueue"/> — never awaited inline, see that type's doc comment.
/// Must run after auth middleware (needs ctx.User) and after static files (so assets never reach it).
/// </summary>
public sealed class SiteVisitMiddleware(RequestDelegate next, SiteVisitQueue queue)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        // Static files never reach here at all (UseStaticFiles short-circuits the pipeline for
        // them, see Program.cs ordering) — this just filters the one dynamic-but-uninteresting
        // endpoint left: the avatar image proxy, which fires once per <img> tag, not per page view.
        if (ctx.Request.Path.StartsWithSegments("/avatar"))
        {
            await next(ctx);
            return;
        }

        const string cookieName = "gk_sid";
        var sessionId = ctx.Request.Cookies[cookieName];
        if (sessionId is null)
        {
            sessionId = Guid.NewGuid().ToString("N");
            ctx.Response.Cookies.Append(cookieName, sessionId, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
            });
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await next(ctx);
        }
        finally
        {
            sw.Stop();

            var userId = long.TryParse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : (long?)null;
            var ip = ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                ?? ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                ?? ctx.Connection.RemoteIpAddress?.ToString();

            queue.Enqueue(new RecordSiteVisitRequest(
                userId,
                ctx.User.Identity?.Name,
                sessionId,
                ip,
                ctx.Request.Path.Value ?? "/",
                ctx.Request.Method,
                ctx.Response.StatusCode,
                sw.ElapsedMilliseconds,
                ctx.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
                ctx.Request.Headers.Referer.ToString() is { Length: > 0 } r ? r : null));
        }
    }
}
