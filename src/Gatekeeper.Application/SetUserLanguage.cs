namespace Gatekeeper.Application;

public sealed record SetUserLanguageCommand(long TelegramUserId, string LanguageCode);

/// <summary>Persists the applicant's explicit language choice from the bot's language-picker, so
/// later bot-generated messages (thank-you, approve/reject) — which can fire long after the
/// survey, from a completely different process — know which language to use.</summary>
public sealed class SetUserLanguageHandler(IUserRepository users, IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(SetUserLanguageCommand cmd, CancellationToken ct = default)
    {
        var user = await users.GetByTelegramIdAsync(cmd.TelegramUserId, ct);
        if (user is null) return;   // shouldn't happen — the user is created on join request — but not fatal either way

        user.SetLanguagePreference(cmd.LanguageCode);
        await unitOfWork.SaveChangesAsync(ct);
    }
}
