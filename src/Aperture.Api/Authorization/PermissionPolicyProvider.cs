using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Aperture.Api.Authorization;

/// <summary>
/// Manufactures a policy per permission on demand, so <c>RequirePermission("deals.read")</c>
/// works without anyone having registered a <c>deals.read</c> policy.
/// <para>
/// The alternative — one <c>AddPolicy</c> call per permission — is a list that has to be kept
/// in step with <see cref="SharedKernel.Authorization.Permissions"/> by hand. The failure is
/// not a missing policy (that throws); it is the day someone "fixes" the throw by falling back
/// to a permissive policy.
/// </para>
/// <para>
/// Policy names outside the <see cref="Prefix"/> namespace fall through to the default
/// provider, so named policies added later still work.
/// </para>
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    /// <summary>Marks a policy name as "a permission", not a hand-registered policy.</summary>
    public const string Prefix = "perm:";

    private readonly DefaultAuthorizationPolicyProvider _fallback;
    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _cache = new(StringComparer.Ordinal);

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    /// <summary>The policy name an endpoint requiring <paramref name="permission"/> carries.</summary>
    public static string PolicyNameFor(string permission) => Prefix + permission;

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        ArgumentNullException.ThrowIfNull(policyName);

        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        var permission = policyName[Prefix.Length..];
        return Task.FromResult<AuthorizationPolicy?>(_cache.GetOrAdd(permission, Build));
    }

    // Built even for an undeclared permission, on purpose: an unsatisfiable requirement denies,
    // whereas returning null here would make ASP.NET report "policy not found" — a 500 that
    // invites the wrong fix.
    private static AuthorizationPolicy Build(string permission) =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
}
