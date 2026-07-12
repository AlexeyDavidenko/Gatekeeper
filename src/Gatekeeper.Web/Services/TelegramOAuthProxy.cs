namespace Gatekeeper.Web.Services;

using Microsoft.Net.Http.Headers;

/// <summary>
/// Transparent single-upstream reverse proxy for the Telegram Login Widget's OAuth handshake,
/// mounted at /tg-oauth on our own domain. Exists because Russian ISPs block/throttle
/// telegram.org and oauth.telegram.org for the browser; this server reaches Telegram fine
/// (confirmed via curl from prod). See wwwroot/lib/telegram/telegram-widget.js for the matching
/// script patch that routes the widget's OAuth traffic through here instead of directly.
/// Deliberately narrow: only forwards what that specific script needs — not a general router.
/// </summary>
public sealed class TelegramOAuthProxy(HttpClient http, ILogger<TelegramOAuthProxy> logger)
{
    // RFC 7230 §6.1 — never forwarded either direction. Content-Length/Content-Type are handled
    // explicitly via .Content.Headers instead of this generic pass, so they're excluded too.
    private static readonly HashSet<string> ExcludedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Content-Length", "Set-Cookie", "Location",
    };

    // Request headers actually needed to reproduce this widget's normal (unproxied) behavior.
    // Cookie is handled separately (filtered, see BuildUpstreamCookieHeader) — never forwarded
    // verbatim, since the browser's Cookie header may also carry this site's OWN auth cookie
    // (e.g. an already-signed-in admin revisiting /login in a stale tab), which must never reach
    // a third party.
    private static readonly string[] ForwardedRequestHeaders =
        ["Accept", "Accept-Language", "User-Agent", "X-Requested-With"];

    private const string TelegramCookiePrefix = "stel_";

    public async Task ProxyAsync(HttpContext ctx, string upstreamPathAndQuery, CancellationToken ct)
    {
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstreamPathAndQuery);

        foreach (var name in ForwardedRequestHeaders)
            if (ctx.Request.Headers.TryGetValue(name, out var value))
                upstreamRequest.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)value!);

        if (BuildUpstreamCookieHeader(ctx.Request.Headers.Cookie) is { } cookie)
            upstreamRequest.Headers.TryAddWithoutValidation("Cookie", cookie);

        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            upstreamRequest.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentType is { } contentType)
                upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            if (ctx.Request.ContentLength is { } length)
                upstreamRequest.Content.Headers.ContentLength = length;
        }

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await http.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Telegram OAuth proxy: upstream call to {Path} failed", upstreamPathAndQuery);
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (upstreamResponse)
        {
            ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;

            if (upstreamResponse.Content.Headers.ContentType is { } responseContentType)
                ctx.Response.ContentType = responseContentType.ToString();

            foreach (var header in upstreamResponse.Headers.Concat(upstreamResponse.Content.Headers))
            {
                if (ExcludedResponseHeaders.Contains(header.Key)) continue;
                ctx.Response.Headers[header.Key] = header.Value.ToArray();
            }

            CopySetCookieHeaders(upstreamResponse, ctx.Response);
            CopyLocationHeader(upstreamResponse, ctx.Response);

            // Deliberately not copying upstream's Content-Length/Transfer-Encoding (excluded
            // above) — Kestrel computes its own framing for the streamed body below.
            var body = await upstreamResponse.Content.ReadAsStreamAsync(ct);
            await body.CopyToAsync(ctx.Response.Body, ct);
        }
    }

    // Allowlist, not blocklist: only Telegram's own cookies cross this proxy.
    private static string? BuildUpstreamCookieHeader(string? incomingCookieHeader)
    {
        if (string.IsNullOrEmpty(incomingCookieHeader)) return null;
        var kept = incomingCookieHeader
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(c => c.StartsWith(TelegramCookiePrefix, StringComparison.Ordinal));
        var joined = string.Join("; ", kept);
        return joined.Length > 0 ? joined : null;
    }

    // Telegram sets stel_ssid with Path=/ and no Domain= (confirmed via curl). Left as-is, the
    // browser would attach it to every request on our whole domain, not just /tg-oauth/*.
    // Re-scope it to the proxy path so it only ever flows back to Telegram.
    private static void CopySetCookieHeaders(HttpResponseMessage upstream, HttpResponse response)
    {
        if (!upstream.Headers.TryGetValues("Set-Cookie", out var raw)) return;
        foreach (var cookie in SetCookieHeaderValue.ParseList(raw.ToList()))
        {
            if (!cookie.Path.HasValue || cookie.Path.Value == "/")
                cookie.Path = "/tg-oauth";
            response.Headers.Append("Set-Cookie", cookie.ToString());
        }
    }

    // Only rewrite Location if it points at the exact upstream we're proxying — oauth.telegram.org
    // itself 302s "/" to a totally different host, core.telegram.org (confirmed via curl), which
    // must pass through untouched, not be rewritten as if it were ours.
    private static void CopyLocationHeader(HttpResponseMessage upstream, HttpResponse response)
    {
        if (upstream.Headers.Location is not { } location) return;
        response.Headers.Location = location.IsAbsoluteUri
            && location.Host.Equals("oauth.telegram.org", StringComparison.OrdinalIgnoreCase)
                ? "/tg-oauth" + location.PathAndQuery
                : location.ToString();
    }
}
