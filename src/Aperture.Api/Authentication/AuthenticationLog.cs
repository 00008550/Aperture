using Aperture.Modules.Access.Authentication;

namespace Aperture.Api.Authentication;

/// <summary>
/// The authentication deny paths, as structured events.
/// <para>
/// Every refusal in this portion returns the same bare 401 on the wire, on purpose. That leaves
/// the log as the only place the five distinct reasons — a malformed token, an inactive tenant,
/// no membership in the named tenant, an inactive user, an invalid or expired token — are
/// distinguishable. Without it nobody can tell revocation from clock skew without a debugger on
/// a running host, and the "same subject, many tenant ids" pattern of a replayed token has no
/// signal at all.
/// </para>
/// <para>
/// Logging only. Audit rows are 001-P6; writing them here would be that portion done badly and
/// early.
/// </para>
/// </summary>
internal static partial class AuthenticationLog
{
    /// <summary>The logger category, so an operator can raise or lower just this stream.</summary>
    public const string Category = "Aperture.Api.Authentication";

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Authentication denied: the token does not carry a well-formed subject and tenant.")]
    public static partial void MalformedToken(ILogger logger);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Authentication denied for subject {Subject} in tenant {TenantId}: {Reason}.")]
    public static partial void PrincipalNotResolved(
        ILogger logger,
        Guid subject,
        Guid tenantId,
        AccessDenialReason reason);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Authentication denied: the bearer token failed validation ({FailureType}).")]
    public static partial void TokenRejected(ILogger logger, string failureType);
}
