using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>The kind of row-level grant. Mirrors the cases of <see cref="DataScope"/>.</summary>
public enum ScopeGrantKind
{
    /// <summary>Deliberately not <c>0</c>. A default-initialised grant must not be a valid kind.</summary>
    Self = 1,
    Team = 2,
    Region = 3,
    Account = 4,
    AllTenant = 5,
}

/// <summary>
/// One row-level grant held by a membership — the persisted form of a <see cref="DataScope"/>.
/// <para>
/// A grant is a row, not a column, for the same reason as <see cref="RolePermission"/>: a user
/// may hold any number of them, and the union of what they hold is what they see.
/// </para>
/// </summary>
public sealed class ScopeGrant : ITenantOwned
{
    private ScopeGrant()
    {
    }

    public ScopeGrant(Guid id, TenantId tenantId, Guid membershipId, ScopeGrantKind kind, Guid? targetId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown scope kind.");
        }

        // The shape of a grant and its meaning must agree. A Team grant with no team is not a
        // narrower grant — read carelessly it becomes a wider one, which is the failure this
        // whole model exists to prevent.
        var needsTarget = kind is ScopeGrantKind.Team or ScopeGrantKind.Region or ScopeGrantKind.Account;
        if (needsTarget && targetId is null)
        {
            throw new ArgumentException($"A {kind} grant requires a target id.", nameof(targetId));
        }

        if (!needsTarget && targetId is not null)
        {
            throw new ArgumentException($"A {kind} grant must not carry a target id.", nameof(targetId));
        }

        Id = id;
        TenantId = tenantId;
        MembershipId = membershipId;
        Kind = kind;
        TargetId = targetId;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid MembershipId { get; private set; }

    public ScopeGrantKind Kind { get; private set; }

    /// <summary>Team, region or account id. Null for <see cref="ScopeGrantKind.Self"/> and
    /// <see cref="ScopeGrantKind.AllTenant"/>, enforced by both the constructor and a check
    /// constraint — the database is the last line, not the only one.</summary>
    public Guid? TargetId { get; private set; }

    /// <summary>
    /// Projects the stored grant onto the in-memory scope from 001-P1. The switch is exhaustive
    /// over a closed hierarchy, so adding a scope kind without handling it here fails to compile
    /// rather than silently granting nothing.
    /// </summary>
    public DataScope ToDataScope(UserId userId) => Kind switch
    {
        ScopeGrantKind.Self => new DataScope.Self(userId),
        ScopeGrantKind.Team => new DataScope.Team(RequireTarget()),
        ScopeGrantKind.Region => new DataScope.Region(RequireTarget()),
        ScopeGrantKind.Account => new DataScope.Account(RequireTarget()),
        ScopeGrantKind.AllTenant => new DataScope.AllTenant(),
        _ => throw new InvalidOperationException($"Unhandled scope kind '{Kind}'."),
    };

    private Guid RequireTarget() =>
        TargetId ?? throw new InvalidOperationException(
            $"A {Kind} grant reached projection with no target id; the row violates its check constraint.");
}
