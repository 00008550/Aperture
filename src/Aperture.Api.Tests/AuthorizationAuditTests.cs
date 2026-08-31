using System.Net;
using System.Net.Http.Headers;
using Aperture.Api.Authentication;
using Aperture.Api.Authorization;
using Aperture.Modules.Access;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aperture.Api.Tests;

/// <summary>
/// 001-P6, the authorization half: a caller who is authenticated but refused the verb produces a
/// 403 <em>and</em> an audit row naming the permission, the actor, the tenant and the correlation
/// id. The console gate (P5) is a courtesy; the audit row is the record that the refusal happened.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuthorizationAuditTests(ApiFixture api)
{
    [Fact]
    public async Task A_permission_denial_writes_an_audit_row_naming_the_permission()
    {
        // Authenticates fine — real, active membership — and holds no permissions, so the gated
        // route refuses the verb.
        var seeded = await api.SeedAsync("audit-denied-user", [], []);

        using var host = await StartHostGatedOnAsync(Permissions.OrdersRead);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/section");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(seeded.TenantId, seeded.UserId));

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var row = await SingleAuditRowAsync(host, seeded.TenantId, seeded.UserId);

        Assert.Equal(AuditCategory.AuthorizationDenied, row.Category);
        Assert.Equal(ActorKind.Human, row.ActorKind);
        Assert.Equal(Permissions.OrdersRead, row.Permission);
        Assert.Equal("no scopes", row.ScopeDecision);
        Assert.Equal("GET /section", row.Action);
        Assert.NotNull(row.CorrelationId);
    }

    [Fact]
    public async Task A_permitted_call_writes_no_denial_row()
    {
        // The other half: a granted verb must not litter the trail with denials. Without this the
        // test above passes against a handler that audits every request.
        var seeded = await api.SeedAsync("audit-permitted-user", [Permissions.OrdersRead], []);

        using var host = await StartHostGatedOnAsync(Permissions.OrdersRead);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/section");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(seeded.TenantId, seeded.UserId));

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
        using (AmbientTenantContext.Begin(seeded.TenantId))
        {
            Assert.False(await db.AuditEvents.AnyAsync(e => e.ActorUserId == seeded.UserId));
        }
    }

    private static async Task<AuditEvent> SingleAuditRowAsync(IHost host, TenantId tenant, UserId user)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();

        // The audit table is tenant-owned, so a read needs an ambient tenant like any other.
        using (AmbientTenantContext.Begin(tenant))
        {
            return await db.AuditEvents.SingleAsync(e => e.ActorUserId == user);
        }
    }

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

/// <summary>
/// Unit tests for the actor-kind marker — the mechanism by which the assistant's calls are
/// marked as such (001-P6). No database: this is pure request-item logic.
/// </summary>
public sealed class AuditActorTests
{
    [Fact]
    public void An_unmarked_request_is_a_human()
    {
        Assert.Equal(ActorKind.Human, AuditActor.KindFor(new DefaultHttpContext()));
    }

    [Fact]
    public void A_request_the_assistant_marked_is_the_assistant()
    {
        var context = new DefaultHttpContext();
        AuditActor.MarkAsAssistant(context);

        Assert.Equal(ActorKind.Assistant, AuditActor.KindFor(context));
    }
}
