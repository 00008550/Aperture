using Aperture.Api.Authentication;
using Aperture.Modules.Access.Authentication;
using Aperture.SharedKernel.Authorization;

namespace Aperture.Api.Endpoints;

/// <summary>
/// One data scope, in the shape the console and the assistant read it. Serialised as a kind and
/// an optional target rather than a polymorphic union, so a new scope kind extends the
/// vocabulary instead of breaking the client's parser.
/// </summary>
public sealed record ScopeResponse(string Kind, Guid? TargetId);

/// <summary>What the caller is: tenant, identity, verbs, rows.</summary>
public sealed record MeResponse(
    Guid TenantId,
    Guid UserId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<ScopeResponse> Scopes);

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Authentication only, no permission: every authenticated user may read their own
        // session. Gating it behind a permission would make a user with no grants unable to
        // discover that they have none, which is the state the console must render explicitly.
        //
        // The handler is a named method rather than an inline lambda so the policy sits next to
        // the route. A long lambda between the two is how a route ends up looking unpoliced to
        // a reviewer skimming the map call.
        app.MapGet("/api/me", GetMe)
            .RequireAuthorization()
            .WithName("GetMe");

        return app;
    }

    private static IResult GetMe(HttpContext http)
    {
        var principal = http.GetAccessPrincipal();

        return Results.Ok(new MeResponse(
            principal.TenantId.Value,
            principal.UserId.Value,
            principal.Email,
            principal.DisplayName,
            // Sorted so the response is stable: an unordered set would make the console's
            // cache key and any snapshot test flap for no reason.
            [.. principal.Permissions.Values.Order(StringComparer.Ordinal)],
            [.. principal.Scopes.Scopes.Select(Describe)
                .OrderBy(s => s.Kind, StringComparer.Ordinal)
                .ThenBy(s => s.TargetId)]));
    }

    // Exhaustive over the closed DataScope hierarchy: adding a case without describing it here
    // throws rather than serialising a scope the client cannot interpret.
    private static ScopeResponse Describe(DataScope scope) => scope switch
    {
        DataScope.Self => new ScopeResponse(nameof(DataScope.Self), null),
        DataScope.Team team => new ScopeResponse(nameof(DataScope.Team), team.TeamId),
        DataScope.Region region => new ScopeResponse(nameof(DataScope.Region), region.RegionId),
        DataScope.Account account => new ScopeResponse(nameof(DataScope.Account), account.AccountId),
        DataScope.AllTenant => new ScopeResponse(nameof(DataScope.AllTenant), null),
        _ => throw new InvalidOperationException($"Unhandled data scope '{scope.GetType().Name}'."),
    };
}
