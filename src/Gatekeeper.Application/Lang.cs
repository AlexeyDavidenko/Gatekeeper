namespace Gatekeeper.Application;

/// <summary>
/// Only two bot-facing languages are supported for now (Telegram's own reported "language_code"
/// covers dozens — anything not Russian falls back to English). Used to pick between the two
/// hardcoded strings wherever the bot generates its own text (welcome/thank-you/decision DMs) —
/// not for admin-authored content like question prompts, which stays whatever the admin typed.
/// </summary>
public static class Lang
{
    public static bool IsRussian(string? languageCode) =>
        languageCode?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true;
}
