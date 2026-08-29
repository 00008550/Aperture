using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>
/// A sales team. Thin on purpose: it exists so a <see cref="ScopeGrant"/> of kind
/// <see cref="ScopeGrantKind.Team"/> has a real foreign key to point at. A grant referencing a
/// team that does not exist is a scope nobody can reason about.
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
