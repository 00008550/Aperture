namespace Aperture.Modules.Access.Authentication;

/// <summary>
/// Why a caller was refused. Every value is a distinct, loggable outcome — a bare null told the
/// API host that resolution failed and nothing about which of four different things went wrong,
/// so in production all four collapsed into one indistinguishable 401.
/// </summary>
public enum AccessDenialReason
{
    /// <summary>Deliberately not <c>0</c>: a default-initialised reason must not be a real one.</summary>
    TenantInactive = 1,

    /// <summary>
    /// The token named a tenant the subject has no active membership in. Repeated across many
    /// tenant ids, this is what a replayed token being probed against other tenants looks like.
    /// </summary>
    NoActiveMembership = 2,

    UserInactive = 3,
}

/// <summary>
/// The outcome of resolving a token's subject and tenant: a principal, or a reason there is
/// none. Never both, and never neither.
/// </summary>
public sealed class AccessPrincipalResolution
{
    private AccessPrincipalResolution(AccessPrincipal? principal, AccessDenialReason? reason)
    {
        Principal = principal;
        Reason = reason;
    }

    /// <summary>The resolved principal, or <see langword="null"/> when the caller was refused.</summary>
    public AccessPrincipal? Principal { get; }

    /// <summary>Why the caller was refused, or <see langword="null"/> when they were not.</summary>
    public AccessDenialReason? Reason { get; }

    /// <summary>
    /// True only when a principal was resolved. Callers branch on this rather than on
    /// <see cref="Reason"/> being null, so a future reason added without a principal cannot be
    /// read as success.
    /// </summary>
    public bool IsGranted => Principal is not null;

    public static AccessPrincipalResolution Granted(AccessPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new AccessPrincipalResolution(principal, reason: null);
    }

    public static AccessPrincipalResolution Denied(AccessDenialReason reason) => new(principal: null, reason);
}
