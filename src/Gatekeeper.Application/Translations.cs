using Gatekeeper.Domain;

namespace Gatekeeper.Application;

public sealed record UpsertTranslationCommand(string Key, string LanguageCode, string Value);

/// <summary>Find-or-create — the same handler serves both "add a brand-new key" (from the seed
/// step's gaps, or a genuinely new string added in code later) and "edit an existing value" (from
/// the /translations page) — the caller never needs to know which case it is.</summary>
public sealed class UpsertTranslationHandler(ITranslationRepository translations)
{
    public async Task HandleAsync(UpsertTranslationCommand cmd, CancellationToken ct = default)
    {
        var existing = await translations.GetAsync(cmd.Key, cmd.LanguageCode, ct);
        if (existing is not null)
        {
            existing.UpdateValue(cmd.Value);
        }
        else
        {
            await translations.AddAsync(Translation.Create(cmd.Key, cmd.LanguageCode, cmd.Value), ct);
        }

        await translations.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Per-request lookup for Application-layer handlers (decision/cancel DMs) — unlike the Bot/Web's
/// TranslationCache, these run once per command rather than per-message, so there's no hot-path
/// reason to cache; a direct repository read is simplest. Same exact-match → "ru" → placeholder
/// fallback chain as TranslationCache.Get, so a missing key behaves identically everywhere.
/// </summary>
public static class TranslationRepositoryExtensions
{
    public static async Task<string> GetValueAsync(
        this ITranslationRepository translations, string key, string languageCode, CancellationToken ct = default)
    {
        var exact = await translations.GetAsync(key, languageCode, ct);
        if (exact is not null) return exact.Value;

        if (languageCode != "ru")
        {
            var ruFallback = await translations.GetAsync(key, "ru", ct);
            if (ruFallback is not null) return ruFallback.Value;
        }

        return $"[{key}]";
    }
}
