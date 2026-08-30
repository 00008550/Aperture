using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>
/// A named set of permissions, owned by one tenant so a customer can shape its own roles.
/// <para>
/// Roles exist only to administer permissions. Nothing outside this module knows a role
/// exists — business logic checks permissions (ARCHITECTURE.md §3), which is what keeps a
/// renamed role from silently changing what code does.
/// </para>
/// </summary>
public sealed class Role : ITenantOwned
{
    private readonly List<RolePermission> _permissions = [];

    private Role()
    {
    }

    public Role(Guid id, TenantId tenantId, string name)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public IReadOnlyCollection<RolePermission> Permissions => _permissions;
}
