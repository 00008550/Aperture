using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aperture.Api.Authentication;
using Aperture.Api.Authorization;
using Aperture.Api.Endpoints;
using Aperture.Modules.Access;
using Aperture.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aperture.Api.Tests;

/// <summary>
/// What the token is allowed to decide, and what it is not.
/// <para>
/// A bearer token says who signed in. Everything else — the tenant is real, the membership is
/// live, the permissions are held — comes out of the access schema. These tests mint tokens
/// that assert more than that, and check the API ignores the surplus.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TokenClaimTrustTests(ApiFixture api)
{
    private const string ForgedPermission = Permissions.OrdersCreditOverride;

    private static IEnumerable<KeyValuePair<string, object>> ForgedPermissionClaim =>
        [new KeyValuePair<string, object>(AccessClaimTypes.Permission, ForgedPermission)];

    [Fact]
    public async Task A_permission_written_into_the_token_does_not_appear_on_the_principal()
    {
        // Holds nothing in the database, and says otherwise in its token.
        var seeded = await api.SeedAsync("claim-forger", [], []);

        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ApiFixture.CreateToken(seeded.TenantId, seeded.UserId, extraClaims: ForgedPermissionClaim));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var me = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        Assert.Empty(me.Permissions);
    }

    [Fact]
    public async Task A_permission_written_into_the_token_does_not_satisfy_RequirePermission()
    {
        var seeded = await api.SeedAsync("claim-forger-403", [], []);

        using var host = await StartHostWithAGuardedRouteAsync();
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/guarded");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ApiFixture.CreateToken(seeded.TenantId, seeded.UserId, extraClaims: ForgedPermissionClaim));

        using var response = await client.SendAsync(request);

        // 403, not 200. The user authenticates — the membership is real — and is then denied the
        // verb, because the grant the token claims does not exist in access.role_permissions.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_permission_granted_in_the_database_does_satisfy_RequirePermission()
    {
        // The other half. Without it, the test above would pass just as well against an API
        // that denied everybody everything.
        var seeded = await api.SeedAsync("credit-controller", [ForgedPermission], []);

        using var host = await StartHostWithAGuardedRouteAsync();
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/guarded");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(seeded.TenantId, seeded.UserId));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A host composed from the same registration extensions as <c>Program.cs</c>, with one
    /// extra route that demands a permission.
    /// <para>
    /// The production host maps no <c>RequirePermission</c> endpoint yet — <c>/api/me</c> needs
    /// authentication only — so there is nothing there to point a 403 test at. Composing the
    /// real registrations rather than stubbing them keeps the thing under test real; the
    /// duplicated pipeline order is the cost, and the architecture test plus the
    /// <c>/api/me</c> tests cover the production pipeline itself.
    /// </para>
    /// </summary>
    private async Task<IHost> StartHostWithAGuardedRouteAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Authentication:Issuer"] = ApiFixture.Issuer,
                ["Authentication:Audience"] = ApiFixture.Audience,
                ["Authentication:SigningKey"] = ApiFixture.SigningKey,
            }).Build();

        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAccessModule(api.ConnectionString);
                    services.AddApertureAuthentication(configuration);
                    services.AddAperturePermissionAuthorization();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseMiddleware<TenantScopeMiddleware>();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints
                        .MapGet("/guarded", () => Results.Ok())
                        .RequirePermission(ForgedPermission));
                }))
            .StartAsync();
    }
}
