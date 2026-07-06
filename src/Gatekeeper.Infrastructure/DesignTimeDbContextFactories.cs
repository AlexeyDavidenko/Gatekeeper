using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Gatekeeper.Infrastructure;

// dotnet-ef needs a way to construct these contexts outside the app's DI container: CatalogDbContext's
// real connection string lives in config the tool never loads, and TenantDbContext's is resolved per
// request from ITenantContext, which doesn't exist at design time. The connection strings below are
// only used to build migrations from the model — they're never connected to.

public sealed class CatalogDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=gatekeeper_catalog;Username=postgres;Password=postgres")
            .Options;
        return new CatalogDbContext(options);
    }
}

// Note: distinct from the runtime TenantDbContextFactory in Tenancy.cs, which builds a
// TenantDbContext for an arbitrary already-known tenant database during the outbox drain.
public sealed class TenantDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql("Host=localhost;Database=gatekeeper_tenant_design;Username=postgres;Password=postgres")
            .Options;
        return new TenantDbContext(options);
    }
}
