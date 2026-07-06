namespace Gatekeeper.Bot;

using System.Collections.Concurrent;

/// <summary>
/// Remembers which community an applicant is being vetted for while their survey runs in a private
/// chat. A DM carries no chat→tenant mapping, so we cache user→tenant when the survey is offered and
/// drop it on completion. State is in-memory (fine at this scale); on restart it can be rehydrated
/// from in-progress sessions via an API call if desired.
/// </summary>
public sealed class TenantRouter
{
    private readonly ConcurrentDictionary<long, long> _userToTenant = new();

    public void Remember(long telegramUserId, long tenantId) => _userToTenant[telegramUserId] = tenantId;

    public long? Resolve(long telegramUserId) =>
        _userToTenant.TryGetValue(telegramUserId, out var tenantId) ? tenantId : null;

    public void Forget(long telegramUserId) => _userToTenant.TryRemove(telegramUserId, out _);
}
