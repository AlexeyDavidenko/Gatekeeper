namespace Gatekeeper.Application;

/// <summary>
/// Builds the applicant header shown on the moderation card. Shared by SubmitAnswerHandler (initial
/// send) and DecideApplicationHandler (edit on decision) so the two never drift apart.
/// </summary>
public static class AdminCardText
{
    public static string BuildHeader(long applicationId, string? username, string? firstName, string? lastName)
    {
        var name = $"{firstName} {lastName}".Trim();
        if (name.Length == 0) name = "—";
        var usernamePart = username is null ? "" : $" (@{username})";
        return $"📋 New application #{applicationId}\n👤 {name}{usernamePart}";
    }
}
