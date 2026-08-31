using System.Diagnostics;
using Aperture.Api.Authentication;
using Aperture.Modules.Access.Auditing;
using Aperture.Modules.Access.Authentication;
using Aperture.Modules.Access.Domain;
using Aperture.SharedKernel.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Aperture.Api.Authorization;

/// <summary>
/// Wraps the framework's authorization result handler and, on a forbidden result, writes one
/// audit row per permission the caller was refused — before the 403 goes out.
/// <para>
/// This seam runs once per request with the final decision in hand, which the per-requirement
/// handler cannot see: <see cref="PermissionAuthorizationHandler"/> knows only "I did not grant
/// this one", not "the request as a whole was denied". A <em>forbidden</em> result carries a
/// resolved principal and an established tenant — the tenant middleware ran before authorization —
/// so the row has an actor, a tenant, the permission, and the correlation id the plan asks for.
/// </para>
/// <para>
/// A <em>challenged</em> result (401, no principal) is not audited here: it has no tenant to
/// attribute a row to. Its counterpart, a validly-signed token that fails to resolve, is audited
/// where it fails — in the principal resolver (001-P3/P6).
/// </para>
/// </summary>
public sealed class AuditingAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _inner = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Forbidden && context.FindAccessPrincipal() is { } principal)
        {
            await AuditForbiddenAsync(context, authorizeResult, principal);
        }

        await _inner.HandleAsync(next, context, policy, authorizeResult);
    }

    private static async Task AuditForbiddenAsync(
        HttpContext context,
        PolicyAuthorizationResult authorizeResult,
        AccessPrincipal principal)
    {
        var deniedPermissions = authorizeResult.AuthorizationFailure?.FailedRequirements
            .OfType<PermissionRequirement>()
            .Select(r => r.Permission)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

        if (deniedPermissions.Length == 0)
        {
            // Forbidden by something other than a permission requirement. Still a denial, but the
            // plan's row shape is keyed on a permission, so there is nothing more useful to say
            // than that one was refused with no permission named.
            deniedPermissions = [null!];
        }

        var audit = context.RequestServices.GetRequiredService<IAuditTrail>();
        var actorKind = AuditActor.KindFor(context);
        var scopeDecision = Describe(principal.Scopes);
        var correlationId = Activity.Current?.Id ?? context.TraceIdentifier;
        var action = $"{context.Request.Method} {context.Request.Path}";

        foreach (var permission in deniedPermissions)
        {
            await audit.RecordAsync(
                new AuditEntry(AuditCategory.AuthorizationDenied, actorKind, principal.UserId)
                {
                    Permission = permission,
                    ScopeDecision = scopeDecision,
                    Reason = "The caller does not hold the required permission.",
                    Action = action,
                    CorrelationId = correlationId,
                },
                context.RequestAborted);
        }
    }

    /// <summary>
    /// A compact, stable summary of the rows the caller could reach — the scopes they hold, or an
    /// explicit note that they hold none. Recorded even on a permission denial so the trail shows
    /// the whole authorization posture, not just the verb that failed.
    /// </summary>
    private static string Describe(DataScopeSet scopes) =>
        scopes.IsEmpty
            ? "no scopes"
            : string.Join(
                ", ",
                scopes.Scopes.Select(s => s.GetType().Name).Order(StringComparer.Ordinal));
}
