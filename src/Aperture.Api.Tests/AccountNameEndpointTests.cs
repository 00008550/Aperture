using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;

namespace Aperture.Api.Tests;

/// <summary>
/// Plan 011-P4, edge 17 on the wire: the contacts grid, the deals grid, <c>GET /api/deals/{id}</c> and the
/// create/add-line/transition responses carry <c>accountName</c> as a JSON property, and a <c>PATCH</c>
/// rename shows up on the next read. The caller deliberately holds <b>no</b> <c>accounts.read</c>: per the
/// user's Q2 decision the name is a label on a row the caller may already read, shown whenever the account
/// is inside the caller's scope. The scope-leak differential (edge 18) and the empty-scope deny (edge 19)
/// are proven at the service level against the same PostgreSQL, on both read paths.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountNameEndpointTests(ApiFixture api)
{
    private HttpClient Client(SeededPrincipal principal)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(principal.TenantId, principal.UserId));
        return client;
    }

    private static async Task<string?> AccountNameOfAsync(HttpResponseMessage response, string? itemsPath = null)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var element = itemsPath is null ? doc.RootElement : doc.RootElement.GetProperty(itemsPath)[0];
        if (element.TryGetProperty("deal", out var nested))
        {
            element = nested;
        }

        // The property must be present on the wire (not merely defaulted by a typed deserializer).
        Assert.True(element.TryGetProperty("accountName", out var name), "accountName missing from payload");
        return name.ValueKind == JsonValueKind.Null ? null : name.GetString();
    }

    [Fact]
    public async Task Grids_detail_and_write_responses_carry_the_current_account_name_without_accounts_read()
    {
        var seeded = await api.SeedAsync(
            "an-no-accounts-read",
            [
                Permissions.AccountsWrite,
                Permissions.ContactsWrite, Permissions.ContactsRead,
                Permissions.DealsWrite, Permissions.DealsRead,
            ],
            [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var accountResp = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = "Wire Name Co",
            taxId = $"TX-AN-{Guid.NewGuid():N}",
            creditLimit = 1000m,
            paymentTermsDays = 30,
            regionId = (Guid?)null,
            teamId = (Guid?)null,
        });
        Assert.Equal(HttpStatusCode.Created, accountResp.StatusCode);
        var account = (await accountResp.Content.ReadFromJsonAsync<AccountView>())!;

        using var contactResp = await client.PostAsJsonAsync(
            $"/api/accounts/{account.Id}/contacts",
            new { name = "Wanda", email = (string?)null, phone = (string?)null, messenger = (string?)null });
        Assert.Equal(HttpStatusCode.Created, contactResp.StatusCode);
        Assert.Equal("Wire Name Co", await AccountNameOfAsync(contactResp));

        using var dealResp = await client.PostAsJsonAsync(
            "/api/deals", new { accountId = account.Id, name = "wire", amount = 100m, discountPct = 0m });
        Assert.Equal(HttpStatusCode.Created, dealResp.StatusCode);
        Assert.Equal("Wire Name Co", await AccountNameOfAsync(dealResp));
        var deal = (await dealResp.Content.ReadFromJsonAsync<DealView>())!;

        using var lineResp = await client.PostAsJsonAsync(
            $"/api/deals/{deal.Id}/lines",
            new { productRef = "SKU", unitPrice = 1m, quantity = 1, priceListVersion = "v1" });
        Assert.Equal(HttpStatusCode.OK, lineResp.StatusCode);
        Assert.Equal("Wire Name Co", await AccountNameOfAsync(lineResp));

        using var moveResp = await client.PostAsJsonAsync(
            $"/api/deals/{deal.Id}/transition", new { targetStage = "qualified" });
        Assert.Equal(HttpStatusCode.OK, moveResp.StatusCode);
        Assert.Equal("Wire Name Co", await AccountNameOfAsync(moveResp));

        // The caller cannot fetch the account itself (no accounts.read) — yet the label shows on its children.
        using var forbidden = await client.GetAsync(new Uri($"/api/accounts/{account.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // Rename through PATCH, then every read path shows the new name.
        using var patch = await client.PatchAsJsonAsync($"/api/accounts/{account.Id}", new
        {
            ownerUserId = seeded.UserId.Value,
            name = "Renamed Co",
            creditLimit = 1000m,
            paymentTermsDays = 30,
            regionId = (Guid?)null,
            teamId = (Guid?)null,
            expectedVersion = account.Version,
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var contacts = await client.GetAsync(new Uri("/api/contacts", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, contacts.StatusCode);
        Assert.Equal("Renamed Co", await AccountNameOfAsync(contacts, "items"));

        using var deals = await client.GetAsync(new Uri("/api/deals", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, deals.StatusCode);
        Assert.Equal("Renamed Co", await AccountNameOfAsync(deals, "items"));

        using var detail = await client.GetAsync(new Uri($"/api/deals/{deal.Id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal("Renamed Co", await AccountNameOfAsync(detail));
    }
}
