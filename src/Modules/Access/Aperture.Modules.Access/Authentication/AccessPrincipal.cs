using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Authentication;

/// <summary>
/// Who the caller is, in one tenant, and what they may do there.
/// <para>
/// This is the <em>resolved</em> answer, not the token's claims. A token says who signed in;
/// only the database says whether that person is still a member of the tenant they named, and
/// what they hold there. Trusting the token for the second question is how a revoked user keeps
/// working until their token expires.
/// </para>
/// <para>
/// Both <see cref="Permissions"/> and <see cref="Scopes"/> are fail-closed values: an
/// unresolved principal carries <see cref="PermissionSet.None"/> and a
/// <see cref="DataScopeSet"/> that admits nothing, rather than null or an empty list a caller
/// might read as "unfiltered" (DOMAIN.md §5.1).
/// </para>
/// </summary>
public sealed record AccessPrincipal(
    TenantId TenantId,
    UserId UserId,
    string Email,
    string DisplayName,
    PermissionSet Permissions,
    DataScopeSet Scopes);
