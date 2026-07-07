using Gatekeeper.Application;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Gatekeeper.Infrastructure;

/// <summary>Mutable, scoped implementation populated once per request by the API middleware.</summary>
public sealed class TenantContext : ITenantContext
{
    public long TenantId { get; private set; }
    public string Slug { get; private set; } = "";
    public string DatabaseName { get; private set; } = "";
    public bool IsResolved { get; private set; }

    public void Set(long tenantId, string slug, string databaseName)
    {
        TenantId = tenantId;
        Slug = slug;
        DatabaseName = databaseName;
        IsResolved = true;
    }
}

/// <summary>
/// Builds a per-tenant connection string from the shared server credentials
/// (env "Postgres:Server" = Host=...;Port=...;Username=postgres;Password=...) plus the tenant's db name.
/// </summary>
public interface ITenantConnectionFactory
{
    string ForDatabase(string databaseName);
}

public sealed class TenantConnectionFactory(IConfiguration config) : ITenantConnectionFactory
{
    private readonly string _server = config["Postgres:Server"]
        ?? throw new InvalidOperationException("Postgres:Server not configured.");

    public string ForDatabase(string databaseName) =>
        new NpgsqlConnectionStringBuilder(_server) { Database = databaseName }.ConnectionString;
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

// --- Repositories (thin; the aggregates hold the behaviour) ---

public sealed class ApplicationRepository(TenantDbContext db) : IApplicationRepository
{
    public Task<Domain.Application?> GetAsync(long id, CancellationToken ct = default) =>
        db.Applications.Include(a => a.Session).Include(a => a.Answers)
          .FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<Domain.Application?> GetActiveForUserAsync(long telegramUserId, CancellationToken ct = default) =>
        db.Applications.Include(a => a.Session)
          .Where(a => a.TelegramUserId == telegramUserId &&
                      (a.Status == ApplicationStatus.SurveyOffered ||
                       a.Status == ApplicationStatus.InSurvey))
          .FirstOrDefaultAsync(ct);

    public async Task AddAsync(Domain.Application application, CancellationToken ct = default) =>
        await db.Applications.AddAsync(application, ct);
}

public sealed class ModerationRepository(TenantDbContext db) : IModerationRepository
{
    public async Task AddAsync(ModerationAction action, CancellationToken ct = default) =>
        await db.ModerationActions.AddAsync(action, ct);
}

/// <summary>Enqueues into the same DbContext so the command commits with the state change.</summary>
public sealed class TelegramCommandQueue(TenantDbContext db) : ITelegramCommandQueue
{
    public void Enqueue(TelegramCommand command) => db.TelegramCommands.Add(command);
}

public sealed class UserRepository(TenantDbContext db) : IUserRepository
{
    public Task<TelegramUser?> GetByTelegramIdAsync(long telegramUserId, CancellationToken ct = default) =>
        db.Users.Include(u => u.History).FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId, ct);

    public async Task AddAsync(TelegramUser user, CancellationToken ct = default) =>
        await db.Users.AddAsync(user, ct);
}

public sealed class QuestionRepository(TenantDbContext db) : IQuestionRepository
{
    public Task<Question?> GetByIdAsync(long id, CancellationToken ct = default) =>
        db.Questions.FirstOrDefaultAsync(q => q.Id == id, ct);

    public Task<Question?> GetFirstActiveAsync(CancellationToken ct = default) =>
        db.Questions.Where(q => q.IsActive).OrderBy(q => q.Position).FirstOrDefaultAsync(ct);

    public Task<Question?> GetNextActiveAsync(int afterPosition, CancellationToken ct = default) =>
        db.Questions.Where(q => q.IsActive && q.Position > afterPosition).OrderBy(q => q.Position).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Question>> GetAllAsync(CancellationToken ct = default) =>
        await db.Questions.OrderBy(q => q.Position).ToListAsync(ct);

    public async Task AddAsync(Question question, CancellationToken ct = default) =>
        await db.Questions.AddAsync(question, ct);
}

public sealed class TenantDirectory(CatalogDbContext catalog) : ITenantDirectory
{
    public async Task<long?> GetAdminChatIdAsync(long tenantId, CancellationToken ct = default)
    {
        var chat = await catalog.TenantChats.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Role == TenantChatRole.AdminGroup, ct);
        return chat?.ChatId;
    }
}

public sealed class TenantCatalogRepository(CatalogDbContext catalog) : ITenantCatalogRepository
{
    public Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        catalog.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);

    public async Task AddAsync(Tenant tenant, CancellationToken ct = default) =>
        await catalog.Tenants.AddAsync(tenant, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => catalog.SaveChangesAsync(ct);
}

/// <summary>
/// Creates and migrates a brand-new tenant database. CREATE DATABASE / existence checks run
/// against Postgres's own always-present "postgres" maintenance database.
/// </summary>
public sealed class TenantProvisioner(
    ITenantConnectionFactory connections, TenantDbContextFactory tenantDbFactory) : ITenantProvisioner
{
    private string AdminConnectionString => connections.ForDatabase("postgres");

    public async Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", conn);
        cmd.Parameters.AddWithValue("name", databaseName);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task CreateDatabaseAsync(string databaseName, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync(ct);
        // Database names can't be parameterized in DDL — quote the identifier instead.
        var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {quoted}", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MigrateAsync(string databaseName, CancellationToken ct = default)
    {
        await using var db = tenantDbFactory.ForDatabase(databaseName);
        await db.Database.MigrateAsync(ct);
    }
}

/// <summary>Builds a tenant context for an arbitrary database — used by the cross-tenant outbox drain.</summary>
public sealed class TenantDbContextFactory(ITenantConnectionFactory connections)
{
    public TenantDbContext ForDatabase(string databaseName) =>
        new(new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connections.ForDatabase(databaseName))
            .Options);
}
