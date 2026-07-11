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
/// Scripted/health-check clients (curl, wget, uptime monitors) hit public pages like /login on a
/// fixed interval forever — found one doing exactly this every ~2 minutes, drowning out real visits
/// (84% of all recorded rows). A missing UA gets the same treatment; real browsers always send one.
/// </summary>
public static class NonBrowserUserAgentDetector
{
    // Prefix match against the token each of these clients puts at the start of its UA string.
    private static readonly string[] Prefixes =
    [
        "curl/", "Wget/", "python-requests/", "python-httpx/", "Go-http-client/",
        "okhttp/", "PostmanRuntime/", "Java/", "Apache-HttpClient/", "axios/", "node-fetch",
    ];

    public static bool IsBot(string? userAgent) =>
        string.IsNullOrWhiteSpace(userAgent) ||
        Prefixes.Any(p => userAgent.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Client IP resolution shared by the HTTP-tracked half of the visit log (SiteVisitMiddleware)
/// and the Blazor-circuit half (CircuitVisitContext), so both agree on one CDN/proxy fallback
/// chain instead of drifting apart.
/// </summary>
public static class SiteVisitClientIp
{
    public static string? Resolve(HttpContext ctx) =>
        ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
        ?? ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
        ?? ctx.Connection.RemoteIpAddress?.ToString();
}

/// <summary>
/// Snapshots the identity/IP/UA/session-id of the request that established this Blazor circuit —
/// captured once, here, because none of these are reliably available for the rest of the
/// circuit's lifetime (SignalR traffic after the initial connect carries no HttpContext). Scoped:
/// DI gives every circuit its own instance, built on first injection (MainLayout), which for an
/// InteractiveServer circuit happens while IHttpContextAccessor.HttpContext still reflects either
/// the prerendering request or the /_blazor connect request that immediately follows it — both
/// carry the same cookies/headers as the page the visitor loaded.
/// </summary>
public sealed class CircuitVisitContext
{
    public long? UserId { get; }
    public string? UserName { get; }
    public string SessionId { get; }
    public string? IpAddress { get; }
    public string? UserAgent { get; }

    public CircuitVisitContext(IHttpContextAccessor accessor)
    {
        var ctx = accessor.HttpContext;
        UserId = long.TryParse(ctx?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : (long?)null;
        UserName = ctx?.User.Identity?.Name;
        // ctx.Items first: a brand-new visitor's cookie was only just written to the response by
        // SiteVisitMiddleware, not readable back off the request — see that middleware for the stash.
        SessionId = ctx?.Items["gk_sid"] as string
            ?? ctx?.Request.Cookies["gk_sid"]
            ?? Guid.NewGuid().ToString("N");
        IpAddress = ctx is null ? null : SiteVisitClientIp.Resolve(ctx);
        UserAgent = ctx?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
    }
}

/// <summary>
/// Captures one page view per request (method, path, status, duration, who, IP, UA, referrer) and
/// hands it to <see cref="SiteVisitQueue"/> — never awaited inline, see that type's doc comment.
/// Must run after auth middleware (needs ctx.User) and after static files (so assets never reach it).
/// Only ever sees the FIRST page load of a circuit and non-Blazor HTTP endpoints (form posts,
/// /auth/*) — everything after that is in-app SignalR navigation, tracked separately by
/// MainLayout's NavigationManager.LocationChanged handler (see CircuitVisitContext above).
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

        // SignalR's own handshake/keepalive traffic for the Blazor circuit — never a page view.
        // Real in-app navigation is tracked from inside the circuit instead (see class doc above).
        if (ctx.Request.Path.StartsWithSegments("/_blazor"))
        {
            await next(ctx);
            return;
        }

        if (NonBrowserUserAgentDetector.IsBot(ctx.Request.Headers.UserAgent.ToString()))
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

        // Lets CircuitVisitContext (built later, while handling this same request) pick up the
        // session id even when it was only just minted above and isn't in the request's cookies yet.
        ctx.Items["gk_sid"] = sessionId;

        var sw = Stopwatch.StartNew();
        try
        {
            await next(ctx);
        }
        finally
        {
            sw.Stop();

            var userId = long.TryParse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : (long?)null;
            var ip = SiteVisitClientIp.Resolve(ctx);

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
