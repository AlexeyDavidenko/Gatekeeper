namespace Gatekeeper.Domain;

/// <summary>
/// A community served by the system. Lives in the shared Catalog DB.
/// Holds only what is needed to route an update to the right tenant database.
/// </summary>
public sealed class Tenant : AggregateRoot
{
    private readonly List<TenantChat> _chats = [];

    public string Slug { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string DatabaseName { get; private set; } = default!;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<TenantChat> Chats => _chats.AsReadOnly();

    private Tenant() { }

    public static Tenant Create(string slug, string name, string databaseName, DateTimeOffset now) => new()
    {
        Slug = slug,
        Name = name,
        DatabaseName = databaseName,
        IsActive = true,
        CreatedAt = now,
    };

    public TenantChat BindChat(long chatId, TenantChatRole role, string? title)
    {
        var chat = TenantChat.Create(Id, chatId, role, title);
        _chats.Add(chat);
        return chat;
    }

    public void Deactivate() => IsActive = false;
}

public sealed class TenantChat : Entity
{
    public long TenantId { get; private set; }
    public long ChatId { get; private set; }
    public TenantChatRole Role { get; private set; }
    public string? Title { get; private set; }

    private TenantChat() { }

    public static TenantChat Create(long tenantId, long chatId, TenantChatRole role, string? title) => new()
    {
        TenantId = tenantId,
        ChatId = chatId,
        Role = role,
        Title = title,
    };
}
