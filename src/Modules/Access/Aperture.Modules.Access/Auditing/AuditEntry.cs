using Aperture.Modules.Access.Domain;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Auditing;

/// <summary>
/// One audit row, in the shape a caller supplies it. The tenant, the id and the timestamp are the
/// audit trail's to stamp — a caller naming its own tenant is exactly how a row lands in the wrong
/// one — so they are not on this record. Everything here is what only the caller knows.
/// </summary>
public sealed record AuditEntry
{
    public AuditEntry(AuditCategory category, ActorKind actorKind, UserId actorUserId)
    {
        Category = category;
        ActorKind = actorKind;
        ActorUserId = actorUserId;
    }

    public AuditCategory Category { get; }

    public ActorKind ActorKind { get; }

    public UserId ActorUserId { get; }

    /// <summary>The permission checked or exercised. Null for an authentication denial.</summary>
    public string? Permission { get; init; }

    /// <summary>A compact description of the scope decision. Null where scope was not evaluated.</summary>
    public string? ScopeDecision { get; init; }

    /// <summary>Why a denial happened. Null for a mutation.</summary>
    public string? Reason { get; init; }

    /// <summary>The route or named command that was attempted.</summary>
    public string? Action { get; init; }

    /// <summary>The correlation id of the request that produced this row.</summary>
    public string? CorrelationId { get; init; }
}
