using Gatekeeper.Domain;
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
        await SeedTranslationsAsync(catalog, ct);

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

    // Fills gaps only — never overwrites a row that already exists, so an Owner's edit via
    // /translations survives every future redeploy. Runs on every API startup; the AsNoTracking
    // existence check keeps repeat runs cheap once the table is fully seeded.
    private static async Task SeedTranslationsAsync(CatalogDbContext catalog, CancellationToken ct)
    {
        var existing = await catalog.Translations.AsNoTracking()
            .Select(t => new { t.Key, t.LanguageCode })
            .ToListAsync(ct);
        var existingSet = existing.Select(e => (e.Key, e.LanguageCode)).ToHashSet();

        var missing = TranslationSeedData.Entries.Where(e => !existingSet.Contains((e.Key, e.LanguageCode)));
        foreach (var (key, languageCode, value) in missing)
            await catalog.Translations.AddAsync(Translation.Create(key, languageCode, value), ct);

        await catalog.SaveChangesAsync(ct);
    }
}
