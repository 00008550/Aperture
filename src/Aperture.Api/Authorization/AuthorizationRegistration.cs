using Aperture.SharedKernel.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Aperture.Api.Authorization;

/// <summary>Registers the permission-driven authorization stack.</summary>
public static class AuthorizationRegistration
{
    public static IServiceCollection AddAperturePermissionAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        // Replace the framework's result handler so every forbidden decision is audited once,
        // with the permission that was refused, before the 403 is written (001-P6).
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();

        services.AddAuthorizationBuilder()
            // Belt and braces with the architecture test: the test fails the build when a route
            // is mapped without a policy, and this makes such a route require authentication
            // anyway in the window before anybody runs the test.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }
}

/// <summary>
/// <c>RequirePermission</c> — the only way an endpoint in this API states what it needs.
/// </summary>
public static class EndpointPermissionExtensions
{
    /// <summary>
    /// Requires <paramref name="permission"/>, which must be declared in
    /// <see cref="Permissions"/>. An undeclared string throws at map time: startup is the one
    /// moment a typo here is cheap to find.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!Permissions.IsDeclared(permission))
        {
            throw new ArgumentException(
                $"'{permission}' is not a declared permission; see Permissions.cs.", nameof(permission));
        }

        return builder.RequireAuthorization(PermissionPolicyProvider.PolicyNameFor(permission));
    }
}
