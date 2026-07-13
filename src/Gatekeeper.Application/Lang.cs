namespace Gatekeeper.Application;

/// <summary>
/// Only two bot-facing languages are supported for now (Telegram's own reported "language_code"
/// covers dozens — anything not Russian falls back to English). Used to pick between the two
/// hardcoded strings wherever the bot generates its own text (welcome/thank-you/decision DMs), and
/// also to pick between an admin-authored question's RU/EN variants (see Question.PromptTextFor/
/// OptionsFor) — falling back to Russian wherever an English variant hasn't been authored yet.
/// </summary>
public static class Lang
{
    public static bool IsRussian(string? languageCode) =>
        languageCode?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Collapses Telegram's full "language_code" range down to the two supported
    /// translation-table language codes — the same "ru" wins, everything else falls back to "en"
    /// rule IsRussian already encodes, just returning the TranslationCache lookup key instead of a bool.</summary>
    public static string Resolve(string? languageCode) => IsRussian(languageCode) ? "ru" : "en";
}
