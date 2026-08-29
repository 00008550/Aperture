using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>
/// A sales team — the target of a <see cref="ScopeGrantKind.Team"/> grant.
/// <para>
/// <b>There is no foreign key from <c>scope_grants.target_id</c> to this table.</b> The column is
/// polymorphic — it points at a team, a region or an account depending on the grant's kind — so
/// no single FK can cover it. The consequence is real and accepted for now: deleting a team
/// leaves grants that reference nothing, and such a grant admits no rows rather than erroring.
/// Failing closed makes that safe but not tidy. 001-P4 owns the cleanup, either by resolving
/// targets on write or by splitting the column per kind.
/// </para>
/// </summary>
public sealed class Team : ITenantOwned
{
    private Team()
    {
    }

    public Team(Guid id, TenantId tenantId, string name)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;
}
