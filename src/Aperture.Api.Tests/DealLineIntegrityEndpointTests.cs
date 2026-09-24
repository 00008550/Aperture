using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;

namespace Aperture.Api.Tests;

/// <summary>
/// Plan 011-P3 over the real host: the add-line outcomes map to their HTTP statuses — a closed deal is 422
/// (edge 12), a frozen-version mismatch is 422 and no version means the frozen one (edge 13), every add moves
/// the deal's <c>version</c> (edge 14), and a stale <c>expectedVersion</c> is 409 with the current deal
/// (edge 16). The concurrent-writer races (edge 15) are proven at the service level in the Sales tests.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DealLineIntegrityEndpointTests(ApiFixture api)
{
    private async Task<HttpClient> ClientAsync(string name)
    {
        var seeded = await api.SeedAsync(
            name,
            [Permissions.AccountsWrite, Permissions.DealsWrite, Permissions.DealsRead],
            [(ScopeGrantKind.Self, null)]);
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(seeded.TenantId, seeded.UserId));
        return client;
    }

    private static async Task<DealView> NewDealAsync(HttpClient client, string taxId)
    {
        using var accountResp = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = $"Acme {taxId}", taxId, creditLimit = 1000m, paymentTermsDays = 30,
            regionId = (Guid?)null, teamId = (Guid?)null,
        });
        Assert.Equal(HttpStatusCode.Created, accountResp.StatusCode);
        var account = await accountResp.Content.ReadFromJsonAsync<AccountView>();

        using var create = await client.PostAsJsonAsync(
            "/api/deals", new { accountId = account!.Id, name = "integrity", amount = 5000m, discountPct = 0m });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<DealView>())!;
    }

    private static Task<HttpResponseMessage> AddLineAsync(
        HttpClient client, Guid dealId, string? priceListVersion = null, uint? expectedVersion = null) =>
        client.PostAsJsonAsync($"/api/deals/{dealId}/lines", new
        {
            productRef = "SKU-1", unitPrice = 100m, quantity = 2, priceListVersion, expectedVersion,
        });

    private static async Task MoveAsync(
        HttpClient client, Guid dealId, string to, string? reason = null, string? priceListVersion = null)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/deals/{dealId}/transition", new { targetStage = to, reason, priceListVersion });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<DealView> QuotedAsync(HttpClient client, string taxId)
    {
        var deal = await NewDealAsync(client, taxId);
        using (var line = await AddLineAsync(client, deal.Id, "v1"))
        {
            Assert.Equal(HttpStatusCode.OK, line.StatusCode);
        }

        await MoveAsync(client, deal.Id, "qualified");
        await MoveAsync(client, deal.Id, "quoted", priceListVersion: "v1");
        return deal;
    }

    private static async Task<DealView> GetAsync(HttpClient client, Guid dealId) =>
        (await client.GetFromJsonAsync<DealView>(new Uri($"/api/deals/{dealId}", UriKind.Relative)))!;

    [Theory]
    [InlineData("won")]
    [InlineData("lost")]
    public async Task Add_line_on_a_terminal_deal_is_422_and_the_line_count_is_unchanged(string terminal)
    {
        using var client = await ClientAsync($"p3-closed-{terminal}");
        var deal = await QuotedAsync(client, $"TX-P3-API-{terminal}");
        await MoveAsync(client, deal.Id, "negotiation");
        await MoveAsync(client, deal.Id, terminal, reason: terminal == "lost" ? "budget" : null);

        using var response = await AddLineAsync(client, deal.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Single((await GetAsync(client, deal.Id)).Lines);
    }

    [Fact]
    public async Task Add_line_after_quote_keeps_the_freeze()
    {
        using var client = await ClientAsync("p3-freeze");
        var deal = await QuotedAsync(client, "TX-P3-API-FREEZE");

        using (var mismatch = await AddLineAsync(client, deal.Id, "v2"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        }

        Assert.Single((await GetAsync(client, deal.Id)).Lines);

        using (var none = await AddLineAsync(client, deal.Id))
        {
            Assert.Equal(HttpStatusCode.OK, none.StatusCode);
            var body = await none.Content.ReadFromJsonAsync<DealView>();
            Assert.All(body!.Lines, l => Assert.Equal("v1", l.PriceListVersion));
        }

        using var same = await AddLineAsync(client, deal.Id, "v1");
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
    }

    [Fact]
    public async Task Add_line_bumps_the_version_and_a_transition_at_the_old_version_is_409()
    {
        using var client = await ClientAsync("p3-bump");
        var deal = await NewDealAsync(client, "TX-P3-API-BUMP");

        using var added = await AddLineAsync(client, deal.Id, expectedVersion: deal.Version);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var after = await added.Content.ReadFromJsonAsync<DealView>();
        Assert.NotEqual(deal.Version, after!.Version);

        using var transition = await client.PostAsJsonAsync(
            $"/api/deals/{deal.Id}/transition", new { targetStage = "qualified", expectedVersion = deal.Version });
        Assert.Equal(HttpStatusCode.Conflict, transition.StatusCode);
    }

    [Fact]
    public async Task Stale_add_line_is_409_with_the_current_deal_and_no_line()
    {
        using var client = await ClientAsync("p3-stale");
        var deal = await NewDealAsync(client, "TX-P3-API-STALE");
        using (var first = await AddLineAsync(client, deal.Id, expectedVersion: deal.Version))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // A double-submit: the second request carries the version the first already consumed.
        using var second = await AddLineAsync(client, deal.Id, expectedVersion: deal.Version);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<DealView>();
        Assert.Equal(deal.Id, body!.Id);
        Assert.Single(body.Lines);
        Assert.Single((await GetAsync(client, deal.Id)).Lines);
    }
}
