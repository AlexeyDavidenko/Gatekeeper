using Microsoft.EntityFrameworkCore;

namespace Gatekeeper.Infrastructure;

/// <summary>
/// Migrates the Catalog DB, then every active tenant DB (database-per-tenant means each one needs
/// its own Database.Migrate() call). Safe to run on every API startup — Migrate() is a no-op once a
/// database already sits on the latest migration.
/// </summary>
public static class MigrationRunner
{
    public static async Task MigrateAllAsync(
        CatalogDbContext catalog, TenantDbContextFactory tenantFactory, CancellationToken ct = default)
    {
        await catalog.Database.MigrateAsync(ct);

        var databaseNames = await catalog.Tenants.AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => t.DatabaseName)
            .ToListAsync(ct);

        foreach (var databaseName in databaseNames)
        {
            await using var tenantDb = tenantFactory.ForDatabase(databaseName);
            await tenantDb.Database.MigrateAsync(ct);
        }
    }
}
