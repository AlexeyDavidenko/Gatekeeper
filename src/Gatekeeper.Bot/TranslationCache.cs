namespace Gatekeeper.Bot;

using Contracts;

/// <summary>
/// Bulk-loaded, periodically-refreshed in-memory copy of the Catalog's Translations table — the
/// bot never looks up a translation per-message, it reads this. Translations change rarely (an
/// Owner editing the site's /translations page) so a 60s refresh cadence trades a small worst-case
/// staleness window for zero per-message API round trips, same tradeoff TenantRouter's rehydration
/// already accepts elsewhere in this codebase.
/// </summary>
public sealed class TranslationCache
{
    private volatile Dictionary<(string Key, string Lang), string> _values = [];

    public void Load(IEnumerable<TranslationDto> translations) =>
        _values = translations.ToDictionary(t => (t.Key, t.LanguageCode), t => t.Value);

    // Exact (key, lang) → fall back to "ru" → fall back to a visible, debuggable placeholder.
    // Never throws — a missing translation should never take down a bot message.
    public string Get(string key, string languageCode)
    {
        if (_values.TryGetValue((key, languageCode), out var exact)) return exact;
        if (_values.TryGetValue((key, "ru"), out var ruFallback)) return ruFallback;
        return $"[{key}]";
    }
}

/// <summary>Same polling shape as OutboxDrainWorker — refresh, sleep, repeat.</summary>
public sealed class TranslationCacheRefreshWorker(
    IGatekeeperApiClient api, TranslationCache cache, ILogger<TranslationCacheRefreshWorker> log) : BackgroundService
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
