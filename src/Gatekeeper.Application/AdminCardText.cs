using Gatekeeper.Domain;

namespace Gatekeeper.Application;

/// <summary>
/// Builds the moderation card text. Shared by SubmitAnswerHandler (initial send),
/// DecideApplicationHandler (edit on decision) and ToggleCardAnswersHandler (show/hide answers)
/// so the pieces never drift apart.
/// </summary>
public static class AdminCardText
{
    public static string BuildHeader(long applicationId, string? username, string? firstName, string? lastName, string? bio)
    {
        var name = $"{firstName} {lastName}".Trim();
        if (name.Length == 0) name = "—";
        var usernamePart = username is null ? "" : $" (@{username})";
        var header = $"📋 New application #{applicationId}\n👤 {name}{usernamePart}";
        if (!string.IsNullOrWhiteSpace(bio))
            header += $"\nℹ️ {bio}";
        return header;
    }

    public static string BuildAnswersBlock(IEnumerable<Answer> answers)
    {
        var ordered = answers.OrderBy(a => a.PositionSnapshot).ToList();
        if (ordered.Count == 0) return "";
        var lines = ordered.Select(a => $"❓ {a.PromptSnapshot}\n{(string.IsNullOrWhiteSpace(a.Text) ? "—" : a.Text)}");
        return "\n\n" + string.Join("\n\n", lines);
    }

    public static string BuildVerdict(bool approve, string by) =>
        $"\n\n{(approve ? "✅ Approved" : "❌ Rejected")} by {by}";
}
