namespace Gatekeeper.Domain;

/// <summary>
/// A single localized string, identified by a dot-separated key ("web.dashboard.title",
/// "bot.welcome") and a language code ("ru"/"en"). Lives in the shared Catalog DB — UI/bot text is
/// identical across all tenants, unlike survey questions, which are already tenant-specific.
/// Bot and Web never touch this table directly; both bulk-load it via the Api into their own
/// in-memory caches (see TranslationCache in each project) and refresh periodically.
/// </summary>
public sealed class Translation : AggregateRoot
{
    public string Key { get; private set; } = default!;
    public string LanguageCode { get; private set; } = default!;
    public string Value { get; private set; } = default!;

    private Translation() { }

    public static Translation Create(string key, string languageCode, string value) => new()
    {
        Key = key,
        LanguageCode = languageCode,
        Value = value,
    };

    public void UpdateValue(string value) => Value = value;
}
