using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>
/// What produced an audited action. Every value is distinct because the point of the audit trail
/// is to answer "who did this" — and "a human clicked it" and "the assistant did it on someone's
/// behalf" are different answers with different accountability (ARCHITECTURE.md §9).
/// </summary>
public enum ActorKind
{
    /// <summary>Deliberately not <c>0</c>: a default-initialised actor kind must not be a real one.</summary>
    Human = 1,

    /// <summary>The in-product AI assistant, acting through the same contracts a human would.</summary>
    Assistant = 2,
}

/// <summary>
/// The kind of thing an audit row records. A denial and a completed mutation are both worth
/// keeping, and telling them apart is the first question anyone reading the trail asks.
/// </summary>
public enum AuditCategory
{
    /// <summary>Deliberately not <c>0</c>: a default-initialised category must not be a real one.</summary>
    AuthenticationDenied = 1,

    /// <summary>A permission gate refused an authenticated caller.</summary>
    AuthorizationDenied = 2,

    /// <summary>A state change the caller was allowed to make.</summary>
    Mutation = 3,
}

/// <summary>
/// One line in the tenant's audit trail: a denial or a mutation, with who, when, and enough of
/// the decision to reconstruct why later.
/// <para>
/// It is <see cref="ITenantOwned"/>, so it inherits the same global query filter as every other
/// row in this module — an audit read in one tenant can never surface another tenant's history,
/// which is the property the P6 tests pin down.
/// </para>
/// </summary>
public sealed class AuditEvent : ITenantOwned
{
    private AuditEvent()
    {
    }

    public AuditEvent(
        Guid id,
        TenantId tenantId,
        DateTimeOffset occurredAt,
        AuditCategory category,
        ActorKind actorKind,
        UserId actorUserId,
        string? permission,
        string? scopeDecision,
        string? reason,
        string? action,
        string? correlationId)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown audit category.");
        }

        if (!Enum.IsDefined(actorKind))
        {
            throw new ArgumentOutOfRangeException(nameof(actorKind), actorKind, "Unknown actor kind.");
        }

        Id = id;
        TenantId = tenantId;
        OccurredAt = occurredAt;
        Category = category;
        ActorKind = actorKind;
        ActorUserId = actorUserId;
        Permission = permission;
        ScopeDecision = scopeDecision;
        Reason = reason;
        Action = action;
        CorrelationId = correlationId;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public AuditCategory Category { get; private set; }

    /// <summary>Human or assistant. This is how "the assistant's calls are marked as such".</summary>
    public ActorKind ActorKind { get; private set; }

    /// <summary>The subject whose token drove the action. Present even on a denial — a denied
    /// caller still has an identity, and it is the one the trail exists to hold accountable.</summary>
    public UserId ActorUserId { get; private set; }

    /// <summary>The permission that was checked or exercised. Null for an authentication denial,
    /// which fails before any permission is reached.</summary>
    public string? Permission { get; private set; }

    /// <summary>A compact description of the scope decision — the rows the caller could see, or
    /// the scope that authorised a mutation. Null where scope was never evaluated.</summary>
    public string? ScopeDecision { get; private set; }

    /// <summary>Why a denial happened, when the category is a denial: the
    /// <c>AccessDenialReason</c> name, or the failed requirement.</summary>
    public string? Reason { get; private set; }

    /// <summary>What was attempted — the route or the named command. Free-form, for the reader.</summary>
    public string? Action { get; private set; }

    /// <summary>Ties this row to the request that produced it, so a denial and everything else
    /// logged under the same trace can be lined up.</summary>
    public string? CorrelationId { get; private set; }
}
