using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Api.Tests;

/// <summary>
/// The account endpoints over the real host (plan 002-P2): every route's permission is actually enforced
/// (missing permission → 403, edge 17), and the happy path runs end to end through EF writes and the
/// reader-role grid. The data-behaviour edges (isolation, empty-scope, union, dedup, pagination,
/// concurrency) are proven at the service level in the Sales test project against the same PostgreSQL.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountEndpointTests(ApiFixture api)
{
    private static object CreateBody(string taxId) => new
    {
        name = $"Acme {taxId}",
        taxId,
        creditLimit = 1000m,
        paymentTermsDays = 30,
        regionId = (Guid?)null,
        teamId = (Guid?)null,
    };

    private HttpClient Client(SeededPrincipal principal)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(principal.TenantId, principal.UserId));
        return client;
    }

    // ---- Authorization enforcement (edge 17) --------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_create_is_401()
    {
        using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/accounts", CreateBody("TX-401"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_accounts_write_cannot_create()
    {
        var seeded = await api.SeedAsync("read-only", [Permissions.AccountsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.PostAsJsonAsync("/api/accounts", CreateBody("TX-403W"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_accounts_read_cannot_list()
    {
        var seeded = await api.SeedAsync("no-read", [Permissions.AccountsWrite], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.GetAsync(new Uri("/api/accounts", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_accounts_write_cannot_update()
    {
        var seeded = await api.SeedAsync("no-write", [Permissions.AccountsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.PatchAsJsonAsync(
            $"/api/accounts/{Guid.NewGuid()}",
            new { ownerUserId = seeded.UserId.Value, name = "x", creditLimit = 1m, paymentTermsDays = 1, regionId = (Guid?)null, teamId = (Guid?)null, expectedVersion = 0u });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Happy path, end to end ---------------------------------------------------------------

    [Fact]
    public async Task Create_then_get_then_list_round_trips_through_EF_and_the_grid()
    {
        var seeded = await api.SeedAsync(
            "full", [Permissions.AccountsWrite, Permissions.AccountsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var create = await client.PostAsJsonAsync("/api/accounts", CreateBody("TX-RT"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<AccountView>();
        Assert.NotNull(created);
        Assert.Equal(seeded.TenantId.Value, created.TenantId);
        Assert.Equal(seeded.UserId.Value, created.OwnerUserId);
        Assert.Equal(created.Id, created.AccountId);

        // GET by id — the EF read path, scope-filtered.
        using var get = await client.GetAsync(new Uri($"/api/accounts/{created.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<AccountView>();
        Assert.Equal(created.Id, fetched!.Id);

        // LIST — the reader-role grid path (ScopedConnection + RLS).
        using var list = await client.GetAsync(new Uri("/api/accounts", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<AccountsPage>();
        Assert.NotNull(page);
        Assert.Contains(page.Items, a => a.Id == created.Id);
    }

    [Fact]
    public async Task A_duplicate_tax_id_is_409_not_500()
    {
        var seeded = await api.SeedAsync("dup", [Permissions.AccountsWrite], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var first = await client.PostAsJsonAsync("/api/accounts", CreateBody("TX-API-DUP"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await client.PostAsJsonAsync("/api/accounts", CreateBody("TX-API-DUP"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_stale_update_is_409()
    {
        var seeded = await api.SeedAsync(
            "stale", [Permissions.AccountsWrite, Permissions.AccountsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var create = await client.PostAsJsonAsync("/api/accounts", CreateBody("TX-STALE"));
        var created = await create.Content.ReadFromJsonAsync<AccountView>();

        var update = new
        {
            ownerUserId = seeded.UserId.Value,
            name = "Renamed",
            creditLimit = 2000m,
            paymentTermsDays = 45,
            regionId = (Guid?)null,
            teamId = (Guid?)null,
            expectedVersion = created!.Version,
        };

        using var firstUpdate = await client.PatchAsJsonAsync($"/api/accounts/{created.Id}", update);
        Assert.Equal(HttpStatusCode.OK, firstUpdate.StatusCode);

        // Replaying the original (now stale) version conflicts.
        using var secondUpdate = await client.PatchAsJsonAsync($"/api/accounts/{created.Id}", update);
        Assert.Equal(HttpStatusCode.Conflict, secondUpdate.StatusCode);
    }

    [Fact]
    public async Task An_account_in_another_tenant_is_not_visible()
    {
        var owner = await api.SeedAsync("owner", [Permissions.AccountsWrite], [(ScopeGrantKind.Self, null)]);
        using var ownerClient = Client(owner);
        using var create = await ownerClient.PostAsJsonAsync("/api/accounts", CreateBody("TX-ISO"));
        var created = await create.Content.ReadFromJsonAsync<AccountView>();

        // A different tenant's user, with read and an AllTenant grant — still must not see across tenants.
        var stranger = await api.SeedAsync(
            "stranger", [Permissions.AccountsRead], [(ScopeGrantKind.AllTenant, null)]);
        using var strangerClient = Client(stranger);

        using var get = await strangerClient.GetAsync(
            new Uri($"/api/accounts/{created!.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }
}
