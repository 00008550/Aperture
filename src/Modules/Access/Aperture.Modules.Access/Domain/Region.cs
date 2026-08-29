using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>
/// A sales region — the target of a <see cref="ScopeGrantKind.Region"/> grant. Thin for the
/// same reason as <see cref="Team"/>.
/// </summary>
public sealed class Region : ITenantOwned
{
    private Region()
    {
    }

    public Region(Guid id, TenantId tenantId, string name)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;
}
