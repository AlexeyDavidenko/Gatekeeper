using Gatekeeper.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// One real, ephemeral Postgres container shared by every test in the "Postgres" collection (see
/// <see cref="PostgresCollection"/>) — for behavior a fake repository can't honestly reproduce:
/// ILIKE search, UTC-bucketed aggregation, and above all xmin-based optimistic concurrency, which is
/// a Postgres system column with no in-memory equivalent.
///
/// postgres:16-alpine — matches the major version actually running in production
/// (deploy/docker-compose.yaml uses postgres:16) at roughly a third less image size and a lighter
/// idle footprint than the Debian-based tag; confirmed pg_trgm still installs fine on it.
///
/// One container for the whole run, not one per test: starting a container is the expensive part
/// (seconds); CREATE DATABASE against an already-running instance is cheap (well under a second),
/// so each test gets its own fresh, migrated, empty tenant database via <see cref="CreateTenantDbAsync"/>
/// for isolation without paying container-startup cost per test.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<TenantDbContext> CreateTenantDbAsync(CancellationToken ct = default)
    {
        var databaseName = $"tenant_test_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(databaseName, ct);

        var db = CreateContextFor(databaseName);
        await db.Database.MigrateAsync(ct);
        return db;
    }

    /// <summary>A second, independent DbContext against the same already-migrated database — used
    /// to simulate two concurrent requests each loading their own in-memory copy of the same row,
    /// or to reach a specific tenant database registered in a <see cref="CreateCatalogDbAsync"/> catalog.</summary>
    public TenantDbContext CreateContextFor(string databaseName)
    {
        var csb = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = databaseName };
        return new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(csb.ConnectionString)
            .Options);
    }

    /// <summary>A fresh, migrated Catalog database — for the cross-tenant /internal/* endpoints,
    /// which scan catalog.Tenants to find every active tenant database to fan out to.</summary>
    public async Task<CatalogDbContext> CreateCatalogDbAsync(CancellationToken ct = default)
    {
        var databaseName = $"catalog_test_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(databaseName, ct);

        var csb = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = databaseName };
        var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(csb.ConnectionString)
            .Options);
        await db.Database.MigrateAsync(ct);
        return db;
    }

    private async Task CreateDatabaseAsync(string databaseName, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(_container.GetConnectionString());
        await admin.OpenAsync(ct);
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
        await create.ExecuteNonQueryAsync(ct);
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
