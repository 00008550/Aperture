using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>Assigns a <see cref="Role"/> to a <see cref="Membership"/>.</summary>
public sealed class MembershipRole : ITenantOwned
{
    private MembershipRole()
    {
    }

    public MembershipRole(Guid id, TenantId tenantId, Guid membershipId, Guid roleId)
    {
        Id = id;
        TenantId = tenantId;
        MembershipId = membershipId;
        RoleId = roleId;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid MembershipId { get; private set; }

    public Guid RoleId { get; private set; }
}
