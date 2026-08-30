namespace Aperture.Api.Authentication;

/// <summary>
/// The claim names this API reads. Raw JWT names, not the WS-Federation URIs the inbound claim
/// mapper would otherwise substitute — <see cref="AuthenticationRegistration"/> turns that
/// mapping off so what is written here is what is matched at runtime.
/// </summary>
public static class AccessClaimTypes
{
    /// <summary>The signed-in user's id.</summary>
    public const string Subject = "sub";

    /// <summary>The tenant the caller is asking to act in. Verified against a membership.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>
    /// One permission the resolved principal holds. Added by this API after resolution —
    /// never read from the incoming token, because a token that could name its own permissions
    /// would make the whole access model advisory.
    /// </summary>
    public const string Permission = "perm";
}
