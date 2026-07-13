namespace Gatekeeper.Web;

using Gatekeeper.Contracts;
using Gatekeeper.Web.Services;

/// <summary>
/// Bulk-loaded, periodically-refreshed in-memory copy of the Catalog's Translations table — same
/// shape as Gatekeeper.Bot's TranslationCache (kept as a separate copy rather than a shared library
/// since Bot and Web don't otherwise share code, and this is barely 20 lines).
/// </summary>
public sealed class TranslationCache
{
    private volatile Dictionary<(string Key, string Lang), string> _values = [];

    public void Load(IEnumerable<TranslationDto> translations) =>
        _values = translations.ToDictionary(t => (t.Key, t.LanguageCode), t => t.Value);

    // Exact (key, lang) → fall back to "ru" → fall back to a visible, debuggable placeholder.
    // Never throws — a missing translation should never take down a page render.
    public string Get(string key, string languageCode)
    {
        if (_values.TryGetValue((key, languageCode), out var exact)) return exact;
        if (_values.TryGetValue((key, "ru"), out var ruFallback)) return ruFallback;
        return $"[{key}]";
    }
}

/// <summary>Same polling shape as SiteVisitFlushWorker/OutboxDrainWorker — refresh, sleep, repeat.</summary>
public sealed class TranslationCacheRefreshWorker(
    AdminApiClient api, TranslationCache cache, ILogger<TranslationCacheRefreshWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                cache.Load(await api.GetTranslationsAsync(stoppingToken));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Translation cache refresh failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }
}

/// <summary>
/// Snapshots the visitor's chosen language (the "gk_lang" cookie, default "ru") once per circuit —
/// same capture-once pattern as CircuitVisitContext, for the same reason: a language switch is a
/// real HTTP redirect (see WebEndpoints' "/lang/{code}"), so a fresh circuit always follows one,
/// making "read the cookie once at circuit start" both correct and sufficient.
/// </summary>
public sealed class LocaleContext
{
    public string LanguageCode { get; }

    public LocaleContext(IHttpContextAccessor accessor)
    {
        var code = accessor.HttpContext?.Request.Cookies["gk_lang"];
        LanguageCode = code is "en" ? "en" : "ru";
    }
}

/// <summary>Terse per-circuit lookup for Razor markup: <c>@T["web.dashboard.title"]</c>.</summary>
public sealed class Translator(TranslationCache cache, LocaleContext locale)
{
    public string this[string key] => cache.Get(key, locale.LanguageCode);
}
