using System.Net;
using System.Net.Http.Headers;
using Aperture.Api.Authentication;
using Aperture.Api.Authorization;
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
/// The console's navigation gate (001-P5) disables what the user cannot do. That is a courtesy,
/// not a control: anyone can re-enable a disabled anchor from the browser's devtools in a
/// second. These tests assert the half that actually protects the data — a caller who reaches
/// the route anyway is refused by the API.
/// <para>
/// The permissions below are exactly the ones <c>frontend/console/src/Navigation.tsx</c> gates
/// its sections on. When a section is added there, its permission belongs here too, so that the
/// gate can never be the only thing standing between a caller and the rows.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ConsoleGatedRouteTests(ApiFixture api)
{
    public static TheoryData<string> ConsoleNavigationPermissions =>
    [
        Permissions.AccountsRead,
        Permissions.ContactsRead,
        Permissions.DealsRead,
        Permissions.OrdersRead,
        Permissions.TimelineRead,
        Permissions.AdminUsers,
    ];

    [Theory]
    [MemberData(nameof(ConsoleNavigationPermissions))]
    public async Task A_route_the_console_disables_still_answers_403_when_it_is_called_anyway(
        string permission)
    {
        // Authenticates fine — a real, active membership — and holds no permissions at all,
        // which is the console state where every navigation item renders locked.
        var seeded = await api.SeedAsync("console-user-without-grants", [], []);

        using var host = await StartHostGatedOnAsync(permission);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/section");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(seeded.TenantId, seeded.UserId));

        using var response = await client.SendAsync(request);

        // 403, not 401 and certainly not 200: identity established, verb denied.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ConsoleNavigationPermissions))]
    public async Task A_route_the_console_enables_answers_200_for_the_user_who_holds_it(
        string permission)
    {
        // The other half. Without it, the theory above passes against an API that denies
        // everyone everything, and the gate would look verified while proving nothing.
        var seeded = await api.SeedAsync("console-user-with-grant", [permission], []);

        using var host = await StartHostGatedOnAsync(permission);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/section");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(seeded.TenantId, seeded.UserId));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_route_the_console_gates_answers_401_with_no_token_at_all()
    {
        // The console's signed-out state. Nothing about "not signed in" may reach a handler.
        using var host = await StartHostGatedOnAsync(Permissions.DealsRead);
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri("/section", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A host composed from the same registration extensions as <c>Program.cs</c>, with one
    /// route standing in for a console section. The production host maps no section routes yet
    /// — they arrive with the Sales and Orders modules — so the stand-in is what a 403 can be
    /// pointed at today, and it runs the real authentication, tenant and authorization pipeline.
    /// </summary>
    private async Task<IHost> StartHostGatedOnAsync(string permission)
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
                        .MapGet("/section", () => Results.Ok())
                        .RequirePermission(permission));
                }))
            .StartAsync();
    }
}
