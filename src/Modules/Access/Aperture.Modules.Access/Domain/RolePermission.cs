using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>
/// One permission string granted to one role.
/// <para>
/// A row per permission rather than a fixed column per permission: the legacy pattern of
/// numbered slots (<c>Right1..Right20</c>) is a ceiling customers hit, and adding the 21st
/// permission should be an insert, not a migration.
/// </para>
/// <para>
/// The value is validated against <see cref="SharedKernel.Authorization.Permissions"/> on the
/// way in. An unrecognised string here would be a permission nothing can ever satisfy.
/// </para>
/// </summary>
public sealed class RolePermission : ITenantOwned
{
    private RolePermission()
    {
    }

    public RolePermission(Guid id, TenantId tenantId, Guid roleId, string permission)
    {
        if (!SharedKernel.Authorization.Permissions.IsDeclared(permission))
        {
            throw new ArgumentException(
                $"'{permission}' is not a declared permission.", nameof(permission));
        }

        Id = id;
        TenantId = tenantId;
        RoleId = roleId;
        Permission = permission;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid RoleId { get; private set; }

    public string Permission { get; private set; } = string.Empty;
}
