using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Authentication;

/// <summary>
/// Turns "this token names user U in tenant T" into what U actually holds in T.
/// <para>
/// The module's public surface for authentication. The API host owns the JWT scheme; it owns
/// none of the access model, and reaches it only through this interface — the same boundary
/// ARCHITECTURE.md §1 draws between modules.
/// </para>
/// </summary>
public interface IAccessPrincipalResolver
{
    /// <summary>
    /// The principal, or <see langword="null"/> when the pairing does not resolve — no active
    /// membership, an inactive user, or an inactive tenant. <b>Null means deny</b>: there is no
    /// partially-resolved principal, because a caller handed one would have to decide what a
    /// missing piece meant, and that decision is the bug.
    /// </summary>
    Task<AccessPrincipal?> ResolveAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken);
}
