namespace Gatekeeper.Web.Endpoints;

using Gatekeeper.Web.Services;

public static class TelegramOAuthProxyEndpoints
{
    public static IEndpointRouteBuilder MapTelegramOAuthProxyEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous by design: unauthenticated /login visitors must reach this before they have
        // any session — see wwwroot/lib/telegram/telegram-widget.js for what calls it.
        app.MapMethods("/tg-oauth/{**path}", ["GET", "POST"], async (
                string? path, HttpContext ctx, TelegramOAuthProxy proxy, CancellationToken ct) =>
            {
                await proxy.ProxyAsync(ctx, "/" + (path ?? "") + ctx.Request.QueryString, ct);
            })
            .AllowAnonymous()
            .DisableAntiforgery();

        return app;
    }
}
