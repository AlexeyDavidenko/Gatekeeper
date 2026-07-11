using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;

namespace Gatekeeper.Application;

/// <summary>Testable clock — never call DateTimeOffset.UtcNow directly in handlers.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// The resolved tenant for the current request. Set by the API's tenant middleware and consumed
/// by the Infrastructure layer to pick the right per-tenant database connection.
/// </summary>
public interface ITenantContext
{
    long TenantId { get; }
    string Slug { get; }
    string DatabaseName { get; }
    bool IsResolved { get; }
}

/// <summary>Commit boundary. Implemented by the tenant DbContext.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IApplicationRepository
{
    Task<Domain.Application?> GetAsync(long id, CancellationToken ct = default);
    Task<Domain.Application?> GetActiveForUserAsync(long telegramUserId, CancellationToken ct = default);
    Task AddAsync(Domain.Application application, CancellationToken ct = default);
}

public interface IUserRepository
{
    Task<TelegramUser?> GetByTelegramIdAsync(long telegramUserId, CancellationToken ct = default);
    Task AddAsync(TelegramUser user, CancellationToken ct = default);
}

public interface IQuestionRepository
{
    Task<Question?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<Question?> GetFirstActiveAsync(CancellationToken ct = default);
    Task<Question?> GetNextActiveAsync(int afterPosition, CancellationToken ct = default);
    Task<IReadOnlyList<Question>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(Question question, CancellationToken ct = default);
}

/// <summary>Resolves cross-tenant routing facts (e.g. the admin group chat) from the catalog.</summary>
public interface ITenantDirectory
{
    Task<long?> GetAdminChatIdAsync(long tenantId, CancellationToken ct = default);
}

/// <summary>
/// Catalog-level persistence for tenant registration — a separate boundary from the per-tenant
/// IUnitOfWork, since the Catalog is the shared control-plane DB, not a tenant's own.
/// </summary>
public interface ITenantCatalogRepository
{
    Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task AddAsync(Tenant tenant, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Physically creates and migrates a new tenant's database.</summary>
public interface ITenantProvisioner
{
    Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken ct = default);
    Task CreateDatabaseAsync(string databaseName, CancellationToken ct = default);
    Task MigrateAsync(string databaseName, CancellationToken ct = default);
}

public interface IModerationRepository
{
    Task AddAsync(ModerationAction action, CancellationToken ct = default);
    Task<ModerationAction?> GetLatestDecisionAsync(long applicationId, CancellationToken ct = default);
}

public interface ISiteVisitRepository
{
    Task AddAsync(SiteVisit visit, CancellationToken ct = default);
    Task<IReadOnlyList<SiteVisit>> GetRecentAsync(int take, CancellationToken ct = default);
}

/// <summary>Adds an outbox command to the current unit of work; committed atomically with the change.</summary>
public interface ITelegramCommandQueue
{
    void Enqueue(TelegramCommand command);
}

/// <summary>Raised when an optimistic-concurrency check fails (another decision won the race).</summary>
public sealed class ConcurrencyConflictException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Raised when an answer is submitted before the survey has been explicitly started via
/// StartSurveyHandler (i.e. the applicant sent a stray message instead of tapping a language-picker
/// button) — see docs/changelog.md 2026-07-11 for why this must reject rather than silently start.</summary>
public sealed class SurveyNotStartedException(string message) : Exception(message);
