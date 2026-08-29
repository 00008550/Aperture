using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>
/// One user's presence in one tenant. Everything a user <em>may do</em> hangs off the
/// membership, never off the user: roles and scope grants are tenant-local by construction,
/// so a grant in one tenant cannot be read as a grant in another.
/// </summary>
public sealed class Membership : ITenantOwned
{
    private readonly List<MembershipRole> _roles = [];
    private readonly List<ScopeGrant> _scopeGrants = [];

    private Membership()
    {
    }

    public Membership(Guid id, TenantId tenantId, UserId userId)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public UserId UserId { get; private set; }

    public bool IsActive { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<MembershipRole> Roles => _roles;

    public IReadOnlyCollection<ScopeGrant> ScopeGrants => _scopeGrants;
}
