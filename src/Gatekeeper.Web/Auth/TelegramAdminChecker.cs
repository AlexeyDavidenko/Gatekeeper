namespace Gatekeeper.Web.Auth;

using Telegram.Bot;
using Telegram.Bot.Types.Enums;

/// <summary>Admin == current member of the tenant's admin group (live check, per the spec).</summary>
public sealed class TelegramAdminChecker(ITelegramBotClient bot)
{
    public async Task<bool> IsAdminAsync(long adminChatId, long userId, CancellationToken ct = default)
    {
        try
        {
            var member = await bot.GetChatMember(adminChatId, userId, ct);
            return member.Status is ChatMemberStatus.Creator
                or ChatMemberStatus.Administrator
                or ChatMemberStatus.Member;
        }
        catch
        {
            return false;  // user not found in the chat / left / kicked
        }
    }
}
