using Gatekeeper.Domain;

namespace Gatekeeper.Application.Tenants;

public sealed record ProvisionTenantCommand(
    string Slug,
    string Name,
    long MainChatId,
    long AdminChatId,
    string? MainChatTitle,
    string? AdminChatTitle);

public sealed record ProvisionTenantResult(long TenantId, string DatabaseName, bool WasCreated);

/// <summary>
/// Idempotent: calling this again for an already-registered slug is a no-op that returns the
/// existing tenant. Also safe to retry after a partial failure — CreateDatabaseAsync is guarded
/// by an existence check and Database.MigrateAsync is itself idempotent.
/// </summary>
public sealed class ProvisionTenantHandler(
    ITenantCatalogRepository catalog, ITenantProvisioner provisioner, IClock clock)
{
    public async Task<ProvisionTenantResult> HandleAsync(ProvisionTenantCommand cmd, CancellationToken ct = default)
    {
        var existing = await catalog.GetBySlugAsync(cmd.Slug, ct);
        if (existing is not null)
            return new ProvisionTenantResult(existing.Id, existing.DatabaseName, WasCreated: false);

        var databaseName = $"tenant_{cmd.Slug}";

        if (!await provisioner.DatabaseExistsAsync(databaseName, ct))
            await provisioner.CreateDatabaseAsync(databaseName, ct);
        await provisioner.MigrateAsync(databaseName, ct);

        var tenant = Tenant.Create(cmd.Slug, cmd.Name, databaseName, clock.UtcNow);
        tenant.BindChat(cmd.MainChatId, TenantChatRole.MainGroup, cmd.MainChatTitle);
        tenant.BindChat(cmd.AdminChatId, TenantChatRole.AdminGroup, cmd.AdminChatTitle);

        await catalog.AddAsync(tenant, ct);
        await catalog.SaveChangesAsync(ct);

        return new ProvisionTenantResult(tenant.Id, databaseName, WasCreated: true);
    }
}
