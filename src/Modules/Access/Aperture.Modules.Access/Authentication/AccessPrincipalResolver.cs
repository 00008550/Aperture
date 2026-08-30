using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Access.Authentication;

/// <summary>
/// Resolves a principal from the access schema.
/// <para>
/// It runs during authentication, before the request has an ambient tenant, so it establishes
/// one for the duration of its own queries — the context's global query filter then applies as
/// it does everywhere else. Every query <em>also</em> names the tenant explicitly. That looks
/// redundant and is not: the filter protects against a forgotten predicate, and the predicate
/// protects against this method one day being called with the wrong ambient scope. The place
/// where the tenant is decided is exactly the place not to rely on ambient state.
/// </para>
/// </summary>
internal sealed class AccessPrincipalResolver(AccessDbContext db) : IAccessPrincipalResolver
{
    public async Task<AccessPrincipalResolution> ResolveAsync(
        TenantId tenantId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        using var scope = AmbientTenantContext.Begin(tenantId);

        // Tenants are not tenant-owned, so nothing filters this one for us.
        var tenantIsActive = await db.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.IsActive, cancellationToken);

        if (!tenantIsActive)
        {
            return AccessPrincipalResolution.Denied(AccessDenialReason.TenantInactive);
        }

        // The membership is the whole authorization decision for "may this token name this
        // tenant". A user with no active membership in T is not a caller in T, whatever their
        // token claims.
        var membershipId = await db.Memberships
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.UserId == userId && m.IsActive)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (membershipId is not { } membership)
        {
            return AccessPrincipalResolution.Denied(AccessDenialReason.NoActiveMembership);
        }

        // Users are global (see Domain/User.cs), so this one is also unfiltered by design.
        var identity = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => new { u.Email, u.DisplayName })
            .FirstOrDefaultAsync(cancellationToken);

        if (identity is null)
        {
            return AccessPrincipalResolution.Denied(AccessDenialReason.UserInactive);
        }

        var permissions = await db.MembershipRoles
            .AsNoTracking()
            .Where(mr => mr.MembershipId == membership && mr.TenantId == tenantId)
            .Join(
                db.RolePermissions.Where(rp => rp.TenantId == tenantId),
                mr => mr.RoleId,
                rp => rp.RoleId,
                (_, rp) => rp.Permission)
            .Distinct()
            .ToListAsync(cancellationToken);

        var grants = await db.ScopeGrants
            .AsNoTracking()
            .Where(g => g.MembershipId == membership && g.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        return AccessPrincipalResolution.Granted(new AccessPrincipal(
            tenantId,
            userId,
            identity.Email,
            identity.DisplayName,
            // Of() drops anything the registry no longer declares, and returns None when that
            // leaves nothing — so a stale grant row cannot outlive its permission.
            PermissionSet.Of(permissions),
            DataScopeSet.Of(tenantId, grants.Select(g => g.ToDataScope(userId)))));
    }
}
