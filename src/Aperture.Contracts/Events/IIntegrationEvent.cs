namespace Aperture.Contracts.Events;

/// <summary>
/// The only shape a module may use to tell another module something happened.
/// Carried by the outbox (ARCHITECTURE.md §6) — never published inline in a transaction.
/// </summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }
    Guid TenantId { get; }
    DateTimeOffset OccurredAt { get; }
}
