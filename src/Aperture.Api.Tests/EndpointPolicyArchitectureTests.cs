using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aperture.Api.Tests;

/// <summary>
/// CLAUDE.md invariant 4 — every endpoint carries an authorization policy — as a test rather
/// than as a habit. <c>scripts/measure.sh gate</c> greps for the same rule; this asserts it
/// against the endpoints the host actually built, which is the version that cannot be fooled by
/// a helper the grep does not recognise.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EndpointPolicyArchitectureTests(ApiFixture api)
{
    /// <summary>
    /// Routes with neither an authorization policy nor an explicit <c>AllowAnonymous</c>.
    /// Anonymity must be stated: an endpoint that is anonymous because nobody thought about it
    /// looks identical to one that is anonymous on purpose.
    /// </summary>
    private static IReadOnlyList<string> RoutesWithoutAPolicy(IEnumerable<Endpoint> endpoints) =>
    [
        .. endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null
                        && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.RoutePattern.RawText ?? e.DisplayName ?? "<unnamed>")
    ];

    [Fact]
    public void Every_mapped_endpoint_carries_an_authorization_policy()
    {
        var endpoints = api.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        // Guards the guard: if the data source came back empty the assertion below would pass
        // while proving nothing.
        Assert.NotEmpty(endpoints);
        Assert.Empty(RoutesWithoutAPolicy(endpoints));
    }

    [Fact]
    public async Task An_endpoint_mapped_without_a_policy_fails_the_architecture_test()
    {
        await using var app = WebApplication.CreateBuilder().Build();
        app.MapGet("/deliberately-unpoliced", () => Results.Ok());

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints);

        Assert.Contains("/deliberately-unpoliced", RoutesWithoutAPolicy(endpoints));
    }
}
