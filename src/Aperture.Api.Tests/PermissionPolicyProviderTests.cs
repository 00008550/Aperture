using System.Security.Claims;
using Aperture.Api.Authentication;
using Aperture.Api.Authorization;
using Aperture.SharedKernel.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aperture.Api.Tests;

/// <summary>
/// The policy provider and its handler, on their own. These are the pieces that decide whether
/// <c>RequirePermission("x")</c> means anything, and every one of their failure modes is a
/// quiet grant rather than a loud error.
/// </summary>
public sealed class PermissionPolicyProviderTests
{
    private static PermissionPolicyProvider Provider() =>
        new(Options.Create(new AuthorizationOptions()));

    private static async Task<bool> IsAllowedAsync(string requiredPermission, params string[] held)
    {
        var identity = new ClaimsIdentity(
            held.Select(p => new Claim(AccessClaimTypes.Permission, p)),
            authenticationType: "test");

        var policy = await Provider().GetPolicyAsync(
            PermissionPolicyProvider.PolicyNameFor(requiredPermission));

        Assert.NotNull(policy);

        var context = new AuthorizationHandlerContext(
            policy.Requirements, new ClaimsPrincipal(identity), resource: null);

        await new PermissionAuthorizationHandler().HandleAsync(context);

        // Both halves matter: the authenticated-user requirement is satisfied separately, so
        // only assert on the permission requirement this handler owns.
        return context.PendingRequirements.OfType<PermissionRequirement>().Any() is false;
    }

    [Fact]
    public async Task A_permission_policy_is_resolved_without_being_registered_by_hand()
    {
        var policy = await Provider().GetPolicyAsync(
            PermissionPolicyProvider.PolicyNameFor(Permissions.DealsRead));

        Assert.NotNull(policy);
        Assert.Contains(
            policy.Requirements.OfType<PermissionRequirement>(),
            r => r.Permission == Permissions.DealsRead);
    }

    [Fact]
    public async Task A_policy_name_outside_the_permission_prefix_falls_through_to_the_default_provider()
    {
        // Not a permission, and not registered anywhere: the default provider answers null,
        // which ASP.NET turns into a startup-time error rather than an open endpoint.
        Assert.Null(await Provider().GetPolicyAsync("some-other-policy"));
    }

    [Fact]
    public async Task A_held_permission_is_granted()
    {
        Assert.True(await IsAllowedAsync(Permissions.DealsRead, Permissions.DealsRead));
    }

    [Fact]
    public async Task A_permission_the_principal_does_not_hold_is_denied()
    {
        Assert.False(await IsAllowedAsync(Permissions.OrdersConfirm, Permissions.DealsRead));
    }

    [Fact]
    public async Task A_principal_holding_nothing_is_denied()
    {
        Assert.False(await IsAllowedAsync(Permissions.DealsRead));
    }

    [Fact]
    public async Task A_permission_claim_differing_only_in_case_is_denied()
    {
        Assert.False(await IsAllowedAsync(Permissions.DealsRead, "Deals.Read"));
    }

    [Fact]
    public async Task An_undeclared_permission_yields_a_policy_that_can_never_be_satisfied()
    {
        // Even when the principal literally holds the string. A policy naming a permission the
        // registry does not declare must lock the endpoint, not open it.
        Assert.False(await IsAllowedAsync("deals.reed", "deals.reed"));
    }

    [Fact]
    public void RequirePermission_rejects_an_undeclared_permission_at_map_time()
    {
        using var app = WebApplication.CreateBuilder().Build();

        var exception = Assert.Throws<ArgumentException>(
            () => app.MapGet("/typo", () => Results.Ok()).RequirePermission("deals.reed"));

        Assert.Contains("not a declared permission", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequirePermission_maps_a_declared_permission_onto_its_policy_name()
    {
        using var app = WebApplication.CreateBuilder().Build();

        app.MapGet("/deals", () => Results.Ok()).RequirePermission(Permissions.DealsRead);

        var endpoint = Assert.Single(
            ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app)
            .DataSources.SelectMany(d => d.Endpoints));

        var authorize = endpoint.Metadata.GetMetadata<IAuthorizeData>();
        Assert.NotNull(authorize);
        Assert.Equal($"perm:{Permissions.DealsRead}", authorize.Policy);
    }
}
