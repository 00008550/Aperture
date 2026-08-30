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
    /// The principal, or the reason there is none — no active membership, an inactive user, or
    /// an inactive tenant. There is no partially-resolved principal: a caller handed one would
    /// have to decide what a missing piece meant, and that decision is the bug.
    /// <para>
    /// The reason exists so the API host can log <em>why</em> it refused. It never reaches the
    /// HTTP response; a 401 that explains itself tells an attacker which half of the guess was
    /// right.
    /// </para>
    /// </summary>
    Task<AccessPrincipalResolution> ResolveAsync(
        TenantId tenantId,
        UserId userId,
        CancellationToken cancellationToken);
}
