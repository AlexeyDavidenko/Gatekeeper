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
}

/// <summary>Resolves cross-tenant routing facts (e.g. the admin group chat) from the catalog.</summary>
public interface ITenantDirectory
{
    Task<long?> GetAdminChatIdAsync(long tenantId, CancellationToken ct = default);
}

public interface IModerationRepository
{
    Task AddAsync(ModerationAction action, CancellationToken ct = default);
}

/// <summary>Adds an outbox command to the current unit of work; committed atomically with the change.</summary>
public interface ITelegramCommandQueue
{
    void Enqueue(TelegramCommand command);
}

/// <summary>Raised when an optimistic-concurrency check fails (another decision won the race).</summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);
