namespace Gatekeeper.Web.Auth;

using Telegram.Bot;
using Telegram.Bot.Types.Enums;

/// <summary>
/// Owner sees everything (incl. the audit log); Member sees the default surface (queue, cards,
/// questions). No separate roles table — Telegram's own admin-group status is the source of truth,
/// same as the login gate itself.
/// </summary>
public enum WebRole { Member, Owner }

/// <summary>Access == current member of the tenant's admin group (live check, per the spec).</summary>
public sealed class TelegramAdminChecker(ITelegramBotClient bot)
{
    public async Task<WebRole?> GetRoleAsync(long adminChatId, long userId, CancellationToken ct = default)
    {
        try
        {
            var member = await bot.GetChatMember(adminChatId, userId, ct);
            return member.Status switch
            {
                ChatMemberStatus.Creator or ChatMemberStatus.Administrator => WebRole.Owner,
                ChatMemberStatus.Member => WebRole.Member,
                _ => null,  // left / kicked / restricted — no access
            };
        }
        catch
        {
            return null;  // user not found in the chat
        }
    }
}
