namespace Gatekeeper.Web;

using Ganss.Xss;

/// <summary>
/// Restricts admin-authored question-prompt HTML down to exactly the inline tags Telegram's
/// ParseMode.Html supports (see https://core.telegram.org/bots/api#html-style). The same
/// sanitized string is stored, rendered on the site (as a Blazor MarkupString), and sent to the
/// bot unchanged — there's no separate "web HTML" vs "Telegram HTML" conversion step.
/// </summary>
public static class RichTextSanitizer
{
    private static readonly HtmlSanitizer Sanitizer = Build();

    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        return Sanitizer.Sanitize(html).Trim();
    }

    private static HtmlSanitizer Build()
    {
        var sanitizer = new HtmlSanitizer
        {
            KeepChildNodes = true, // a stray disallowed wrapper (e.g. pasted <span>) loses the tag, not the text
        };

        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[] { "b", "strong", "i", "em", "u", "s", "strike", "del", "code", "pre", "a" })
            sanitizer.AllowedTags.Add(tag);

        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.Add("href");

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();

        return sanitizer;
    }
}
