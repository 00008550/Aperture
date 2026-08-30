using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aperture.Api.Endpoints;
using Aperture.Modules.Access.Domain;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Api.Tests;

/// <summary>
/// The P3 test list, by name. Every case here is about what happens when the token and the
/// database disagree — the only interesting question authentication has.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MeEndpointTests(ApiFixture api)
{
    private static HttpRequestMessage Me(string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    [Fact]
    public async Task An_unauthenticated_request_to_api_me_is_rejected_with_401()
    {
        using var client = api.CreateClient();

        using var response = await client.SendAsync(Me());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_returns_the_tenant_user_permissions_and_scopes()
    {
        var teamId = Guid.NewGuid();
        var seeded = await api.SeedAsync(
            "valid",
            [Permissions.DealsRead, Permissions.OrdersRead],
            [(ScopeGrantKind.Self, null), (ScopeGrantKind.Team, teamId)]);

        using var client = api.CreateClient();
        using var response = await client.SendAsync(
            Me(ApiFixture.CreateToken(seeded.TenantId, seeded.UserId)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var me = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        Assert.Equal(seeded.TenantId.Value, me.TenantId);
        Assert.Equal(seeded.UserId.Value, me.UserId);
        Assert.Equal([Permissions.DealsRead, Permissions.OrdersRead], me.Permissions);
        Assert.Equal(
            [new ScopeResponse("Self", null), new ScopeResponse("Team", teamId)],
            me.Scopes);
    }

    [Fact]
    public async Task A_token_naming_a_tenant_the_user_does_not_belong_to_is_rejected()
    {
        var seeded = await api.SeedAsync("home", [Permissions.DealsRead], []);
        var stranger = await api.SeedAsync("other", [Permissions.DealsRead], []);

        using var client = api.CreateClient();

        // A perfectly well-signed token: only the tenant is one this user has no membership in.
        using var response = await client.SendAsync(
            Me(ApiFixture.CreateToken(stranger.TenantId, seeded.UserId)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_naming_a_tenant_that_does_not_exist_is_rejected()
    {
        var seeded = await api.SeedAsync("ghost-tenant", [Permissions.DealsRead], []);

        using var client = api.CreateClient();
        using var response = await client.SendAsync(
            Me(ApiFixture.CreateToken(TenantId.New(), seeded.UserId)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_for_a_deactivated_membership_is_rejected()
    {
        var seeded = await api.SeedAsync(
            "revoked", [Permissions.DealsRead], [], membershipIsActive: false);

        using var client = api.CreateClient();
        using var response = await client.SendAsync(
            Me(ApiFixture.CreateToken(seeded.TenantId, seeded.UserId)));

        // The token is still valid and unexpired. Revocation has to bite before it expires,
        // which is why the principal is resolved per request rather than trusted from claims.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_for_a_deactivated_tenant_is_rejected()
    {
        var seeded = await api.SeedAsync(
            "suspended", [Permissions.DealsRead], [], tenantIsActive: false);

        using var client = api.CreateClient();
        using var response = await client.SendAsync(
            Me(ApiFixture.CreateToken(seeded.TenantId, seeded.UserId)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_rejected()
    {
        var seeded = await api.SeedAsync("forged", [Permissions.DealsRead], []);

        using var client = api.CreateClient();
        using var response = await client.SendAsync(Me(ApiFixture.CreateToken(
            seeded.TenantId,
            seeded.UserId,
            signingKey: "a-completely-different-key-of-sufficient-length")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_rejected()
    {
        var seeded = await api.SeedAsync("wrong-issuer", [Permissions.DealsRead], []);

        using var client = api.CreateClient();
        using var response = await client.SendAsync(Me(ApiFixture.CreateToken(
            seeded.TenantId, seeded.UserId, issuer: "https://elsewhere.example/")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var seeded = await api.SeedAsync("expired", [Permissions.DealsRead], []);

        using var client = api.CreateClient();
        using var response = await client.SendAsync(Me(ApiFixture.CreateToken(
            seeded.TenantId, seeded.UserId, expires: DateTime.UtcNow.AddHours(-1))));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_with_no_grants_authenticates_and_reports_nothing_held()
    {
        var seeded = await api.SeedAsync("bare", [], []);

        using var client = api.CreateClient();
        using var response = await client.SendAsync(
            Me(ApiFixture.CreateToken(seeded.TenantId, seeded.UserId)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var me = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);

        // Empty, not absent, and certainly not "everything" — DOMAIN.md §5.1 reaching the wire.
        Assert.Empty(me.Permissions);
        Assert.Empty(me.Scopes);
    }

    [Fact]
    public async Task The_health_probes_stay_anonymous()
    {
        using var client = api.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
