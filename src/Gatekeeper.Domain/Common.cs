namespace Gatekeeper.Domain;

/// <summary>Marker for things that happened in the domain and may trigger side effects.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredOn { get; }
}

/// <summary>Thrown when a caller attempts an operation the domain forbids (e.g. an illegal state transition).</summary>
public sealed class DomainException(string message) : Exception(message);

public abstract class Entity
{
    public long Id { get; protected set; }

    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent e) => _domainEvents.Add(e);
    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>An aggregate root is the only entry point through which its child entities may be modified.</summary>
public abstract class AggregateRoot : Entity;
