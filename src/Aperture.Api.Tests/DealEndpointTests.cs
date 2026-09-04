using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;

namespace Aperture.Api.Tests;

/// <summary>
/// The deal endpoints over the real host (plan 002-P4): every route's permission is actually enforced
/// (missing permission → 401/403, edge 17), and the happy path runs end to end — create under an account,
/// list through the reader-role grid, read one with its lines, add a line. The data-behaviour edges
/// (one-account rule, scope inheritance, isolation, the edge-8 re-stamp) are proven at the service level in
/// the Sales test project against the same PostgreSQL.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DealEndpointTests(ApiFixture api)
{
    private HttpClient Client(SeededPrincipal principal)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(principal.TenantId, principal.UserId));
        return client;
    }

    private static object AccountBody(string taxId) => new
    {
        name = $"Acme {taxId}",
        taxId,
        creditLimit = 1000m,
        paymentTermsDays = 30,
        regionId = (Guid?)null,
        teamId = (Guid?)null,
    };

    private static object DealBody(Guid accountId, string name) => new
    {
        accountId,
        name,
        amount = 5000m,
        discountPct = 5m,
    };

    // ---- Authorization enforcement (edge 17) --------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_create_is_401()
    {
        using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/deals", DealBody(Guid.NewGuid(), "anon"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_deals_write_cannot_create()
    {
        var seeded = await api.SeedAsync(
            "d-read-only", [Permissions.DealsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.PostAsJsonAsync("/api/deals", DealBody(Guid.NewGuid(), "d403w"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_deals_read_cannot_list()
    {
        var seeded = await api.SeedAsync(
            "d-no-read", [Permissions.DealsWrite], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.GetAsync(new Uri("/api/deals", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_deals_write_cannot_add_a_line()
    {
        var seeded = await api.SeedAsync(
            "d-no-line", [Permissions.DealsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.PostAsJsonAsync(
            $"/api/deals/{Guid.NewGuid()}/lines",
            new { productRef = "X", unitPrice = 1m, quantity = 1, priceListVersion = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Happy path, end to end ---------------------------------------------------------------

    [Fact]
    public async Task Create_under_an_account_then_list_then_get_then_add_line_round_trips()
    {
        var seeded = await api.SeedAsync(
            "d-full",
            [Permissions.AccountsWrite, Permissions.DealsWrite, Permissions.DealsRead],
            [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var accountResp = await client.PostAsJsonAsync("/api/accounts", AccountBody("TX-D-RT"));
        Assert.Equal(HttpStatusCode.Created, accountResp.StatusCode);
        var account = await accountResp.Content.ReadFromJsonAsync<AccountView>();

        using var create = await client.PostAsJsonAsync("/api/deals", DealBody(account!.Id, "round-trip"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var deal = await create.Content.ReadFromJsonAsync<DealView>();
        Assert.NotNull(deal);
        Assert.Equal(seeded.TenantId.Value, deal.TenantId);
        Assert.Equal(account.Id, deal.AccountId);
        // Scope inheritance: the deal's owner is the account's owner (the seeded caller).
        Assert.Equal(seeded.UserId.Value, deal.OwnerUserId);
        Assert.Equal("new", deal.Stage);

        // The grid shows it.
        using var list = await client.GetAsync(new Uri("/api/deals", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<DealsPage>();
        Assert.Contains(page!.Items, d => d.Id == deal.Id);

        // Add a line, then read the deal back with its line.
        using var addLine = await client.PostAsJsonAsync(
            $"/api/deals/{deal.Id}/lines",
            new { productRef = "SKU-1", unitPrice = 100m, quantity = 2, priceListVersion = "v1" });
        Assert.Equal(HttpStatusCode.OK, addLine.StatusCode);

        using var get = await client.GetAsync(new Uri($"/api/deals/{deal.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var full = await get.Content.ReadFromJsonAsync<DealView>();
        Assert.Single(full!.Lines);
        Assert.Equal("SKU-1", full.Lines[0].ProductRef);
    }

    [Fact]
    public async Task Create_against_an_account_the_caller_cannot_see_is_404()
    {
        var owner = await api.SeedAsync(
            "d-owner", [Permissions.AccountsWrite], [(ScopeGrantKind.Self, null)]);
        using var ownerClient = Client(owner);
        using var accountResp = await ownerClient.PostAsJsonAsync("/api/accounts", AccountBody("TX-D-HIDDEN"));
        var account = await accountResp.Content.ReadFromJsonAsync<AccountView>();

        var stranger = await api.SeedAsync(
            "d-strangr", [Permissions.DealsWrite], [(ScopeGrantKind.Self, null)]);
        using var strangerClient = Client(stranger);

        using var create = await strangerClient.PostAsJsonAsync(
            "/api/deals", DealBody(account!.Id, "intruder"));

        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }
}
