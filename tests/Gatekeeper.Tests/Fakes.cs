using Gatekeeper.Application;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;

namespace Gatekeeper.Tests;

/// <summary>
/// Hand-rolled in-memory fakes for the Application layer's ports (see
/// Gatekeeper.Application/Abstractions.cs) — no mocking framework in this repo, and these
/// interfaces are small enough that a fake is less ceremony than mocks per test.
/// </summary>
public static class FakeIds
{
    /// <summary>
    /// Entity.Id has only a protected setter — EF assigns it on SaveChanges in production, nothing
    /// else is supposed to. Repository fakes mimic that via reflection on AddAsync; this is the one
    /// place it's acceptable to reach around the domain's own encapsulation, purely for test plumbing.
    /// </summary>
    public static void Assign(Entity entity, long id) =>
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(entity, id);
}

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCount++;
        return Task.FromResult(1);
    }
}

public sealed class FakeTelegramCommandQueue : ITelegramCommandQueue
{
    public List<TelegramCommand> Enqueued { get; } = [];

    public void Enqueue(TelegramCommand command) => Enqueued.Add(command);
}

public sealed class FakeTenantContext(long tenantId = 1, string slug = "test", string databaseName = "tenant_test")
    : ITenantContext
{
    public long TenantId { get; } = tenantId;
    public string Slug { get; } = slug;
    public string DatabaseName { get; } = databaseName;
    public bool IsResolved => true;
}

public sealed class FakeTenantDirectory(long? adminChatId) : ITenantDirectory
{
    public Task<long?> GetAdminChatIdAsync(long tenantId, CancellationToken ct = default) =>
        Task.FromResult(adminChatId);
}

public sealed class FakeApplicationRepository : IApplicationRepository
{
    private long _nextId = 1;
    public Dictionary<long, Domain.Application> Store { get; } = [];

    public Task<Domain.Application?> GetAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Store.GetValueOrDefault(id));

    public Task<Domain.Application?> GetActiveForUserAsync(long telegramUserId, CancellationToken ct = default) =>
        Task.FromResult(Store.Values.FirstOrDefault(a =>
            a.TelegramUserId == telegramUserId &&
            a.Status is ApplicationStatus.SurveyOffered or ApplicationStatus.InSurvey));

    public Task AddAsync(Domain.Application application, CancellationToken ct = default)
    {
        FakeIds.Assign(application, _nextId++);
        Store[application.Id] = application;
        return Task.CompletedTask;
    }

    /// <summary>Seeds an application that's already past creation (e.g. AwaitingReview) without
    /// going through AddAsync's auto-increment — useful when the test needs a specific known id.</summary>
    public void Seed(Domain.Application application, long id)
    {
        FakeIds.Assign(application, id);
        Store[id] = application;
        _nextId = Math.Max(_nextId, id + 1);
    }
}

public sealed class FakeUserRepository : IUserRepository
{
    private long _nextId = 1;
    public Dictionary<long, TelegramUser> Store { get; } = [];

    public Task<TelegramUser?> GetByTelegramIdAsync(long telegramUserId, CancellationToken ct = default) =>
        Task.FromResult(Store.Values.FirstOrDefault(u => u.TelegramUserId == telegramUserId));

    public Task AddAsync(TelegramUser user, CancellationToken ct = default)
    {
        FakeIds.Assign(user, _nextId++);
        Store[user.Id] = user;
        return Task.CompletedTask;
    }

    public void Seed(TelegramUser user)
    {
        FakeIds.Assign(user, _nextId++);
        Store[user.Id] = user;
    }
}

public sealed class FakeQuestionRepository : IQuestionRepository
{
    private long _nextId = 1;
    public List<Question> Store { get; } = [];

    public void Seed(Question question)
    {
        FakeIds.Assign(question, _nextId++);
        Store.Add(question);
    }

    public Task<Question?> GetByIdAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Store.FirstOrDefault(q => q.Id == id));

    public Task<Question?> GetFirstActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(Store.Where(q => q.IsActive).OrderBy(q => q.Position).FirstOrDefault());

    public Task<Question?> GetNextActiveAsync(int afterPosition, CancellationToken ct = default) =>
        Task.FromResult(Store.Where(q => q.IsActive && q.Position > afterPosition)
            .OrderBy(q => q.Position).FirstOrDefault());

    public Task<IReadOnlyList<Question>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Question>>(Store.OrderBy(q => q.Position).ToList());

    public Task AddAsync(Question question, CancellationToken ct = default)
    {
        FakeIds.Assign(question, _nextId++);
        Store.Add(question);
        return Task.CompletedTask;
    }
}

public sealed class FakeModerationRepository : IModerationRepository
{
    private long _nextId = 1;
    public List<ModerationAction> Store { get; } = [];

    public Task AddAsync(ModerationAction action, CancellationToken ct = default)
    {
        FakeIds.Assign(action, _nextId++);
        Store.Add(action);
        return Task.CompletedTask;
    }

    public Task<ModerationAction?> GetLatestDecisionAsync(long applicationId, CancellationToken ct = default) =>
        Task.FromResult(Store
            .Where(a => a.ApplicationId == applicationId &&
                        (a.Action == ModerationActionType.Approve || a.Action == ModerationActionType.Reject))
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault());

    public Task<ModerationAction?> GetAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Store.FirstOrDefault(a => a.Id == id));

    public Task<IReadOnlyList<ModerationAction>> GetActiveOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ModerationAction>>(
            Store.Where(a => !a.IsArchived && a.CreatedAt < cutoff).ToList());

    /// <summary>Seeds an action with a specific known id, bypassing AddAsync's auto-increment.</summary>
    public void Seed(ModerationAction action, long id)
    {
        FakeIds.Assign(action, id);
        Store.Add(action);
        _nextId = Math.Max(_nextId, id + 1);
    }
}

public sealed class FakeTranslationRepository : ITranslationRepository
{
    private long _nextId = 1;
    public List<Translation> Store { get; } = [];

    public Task<IReadOnlyList<Translation>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Translation>>(Store);

    public Task<Translation?> GetAsync(string key, string languageCode, CancellationToken ct = default) =>
        Task.FromResult(Store.FirstOrDefault(t => t.Key == key && t.LanguageCode == languageCode));

    public Task AddAsync(Translation translation, CancellationToken ct = default)
    {
        FakeIds.Assign(translation, _nextId++);
        Store.Add(translation);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeEvidenceStorage : IEvidenceStorage
{
    public List<(string StoragePath, string ContentType, long SizeBytes)> Saved { get; } = [];

    public Task<(string StoragePath, string ContentHash, long SizeBytes)> SaveAsync(
        Stream content, string contentType, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var path = $"evidence/{hash}";
        Saved.Add((path, contentType, bytes.LongLength));
        return Task.FromResult((path, hash, bytes.LongLength));
    }

    public Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(Saved.Any(s => s.StoragePath == storagePath) ? new MemoryStream() : null);
}
