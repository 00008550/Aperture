using Aperture.Api.Authentication;
using Aperture.SharedKernel.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Aperture.Api.Authorization;

/// <summary>
/// Satisfies a <see cref="PermissionRequirement"/> from the permission claims the token
/// validation step attached.
/// <para>
/// Nothing here has an else branch. A handler that does not call <see cref="AuthorizationHandlerContext.Succeed"/>
/// denies, so every path that is not an explicit grant — an undeclared permission, a missing
/// claim, a claim differing in case — ends in a denial without anybody writing one.
/// </para>
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // A permission the registry does not declare can never be held, so it can never be
        // granted. This is what makes a typo in a policy name lock an endpoint rather than
        // open it.
        if (Permissions.IsDeclared(requirement.Permission)
            && context.User.HasClaim(AccessClaimTypes.Permission, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
