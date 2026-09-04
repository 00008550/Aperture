using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;

namespace Aperture.Api.Tests;

/// <summary>
/// The contact endpoints over the real host (plan 002-P3): every route's permission is actually enforced
/// (missing permission → 403, edge 17), and the happy path runs end to end — create under a parent account,
/// list through the reader-role grid, depart (mark, never delete). The data-behaviour edges (one-account
/// rule, scope inheritance, departed-not-deleted, isolation) are proven at the service level in the Sales
/// test project against the same PostgreSQL.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ContactEndpointTests(ApiFixture api)
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

    private static object ContactBody(string name) => new
    {
        name,
        email = $"{name}@example.com",
        phone = (string?)null,
        messenger = (string?)null,
    };

    // ---- Authorization enforcement (edge 17) --------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_create_is_401()
    {
        using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync(
            $"/api/accounts/{Guid.NewGuid()}/contacts", ContactBody("anon"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_contacts_write_cannot_create()
    {
        var seeded = await api.SeedAsync(
            "c-read-only", [Permissions.ContactsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.PostAsJsonAsync(
            $"/api/accounts/{Guid.NewGuid()}/contacts", ContactBody("c403w"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_contacts_read_cannot_list()
    {
        var seeded = await api.SeedAsync(
            "c-no-read", [Permissions.ContactsWrite], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.GetAsync(new Uri("/api/contacts", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_contacts_write_cannot_depart()
    {
        var seeded = await api.SeedAsync(
            "c-no-depart", [Permissions.ContactsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.PostAsync(
            new Uri($"/api/contacts/{Guid.NewGuid()}/depart", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Happy path, end to end ---------------------------------------------------------------

    [Fact]
    public async Task Create_under_an_account_then_list_then_depart_round_trips()
    {
        var seeded = await api.SeedAsync(
            "c-full",
            [Permissions.AccountsWrite, Permissions.ContactsWrite, Permissions.ContactsRead],
            [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        // A parent account (self-owned, so the caller's Self grant admits it).
        using var accountResp = await client.PostAsJsonAsync("/api/accounts", AccountBody("TX-C-RT"));
        Assert.Equal(HttpStatusCode.Created, accountResp.StatusCode);
        var account = await accountResp.Content.ReadFromJsonAsync<AccountView>();

        using var create = await client.PostAsJsonAsync(
            $"/api/accounts/{account!.Id}/contacts", ContactBody("dave"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var contact = await create.Content.ReadFromJsonAsync<ContactView>();
        Assert.NotNull(contact);
        Assert.Equal(seeded.TenantId.Value, contact.TenantId);
        Assert.Equal(account.Id, contact.AccountId);
        // Scope inheritance: the contact's owner is the account's owner (the seeded caller).
        Assert.Equal(seeded.UserId.Value, contact.OwnerUserId);
        Assert.False(contact.IsDeparted);

        // The active grid shows it.
        using var list = await client.GetAsync(new Uri("/api/contacts", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<ContactsPage>();
        Assert.Contains(page!.Items, c => c.Id == contact.Id);

        // Depart marks the row; it is then gone from the active grid but present in history.
        using var depart = await client.PostAsync(
            new Uri($"/api/contacts/{contact.Id}/depart", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.OK, depart.StatusCode);
        var departed = await depart.Content.ReadFromJsonAsync<ContactView>();
        Assert.True(departed!.IsDeparted);

        using var active = await client.GetAsync(new Uri("/api/contacts", UriKind.Relative));
        var activePage = await active.Content.ReadFromJsonAsync<ContactsPage>();
        Assert.DoesNotContain(activePage!.Items, c => c.Id == contact.Id);

        using var history = await client.GetAsync(
            new Uri("/api/contacts?includeDeparted=true", UriKind.Relative));
        var historyPage = await history.Content.ReadFromJsonAsync<ContactsPage>();
        Assert.Contains(historyPage!.Items, c => c.Id == contact.Id);
    }

    [Fact]
    public async Task Create_under_an_account_the_caller_cannot_see_is_404()
    {
        // The account is created by one self-owning user; a different user (also Self, different id) cannot
        // attach a contact to it — the one-account rule fails closed as not-found.
        var owner = await api.SeedAsync(
            "c-owner", [Permissions.AccountsWrite], [(ScopeGrantKind.Self, null)]);
        using var ownerClient = Client(owner);
        using var accountResp = await ownerClient.PostAsJsonAsync("/api/accounts", AccountBody("TX-C-HIDDEN"));
        var account = await accountResp.Content.ReadFromJsonAsync<AccountView>();

        var stranger = await api.SeedAsync(
            "c-strangr", [Permissions.ContactsWrite], [(ScopeGrantKind.Self, null)]);
        using var strangerClient = Client(stranger);

        using var create = await strangerClient.PostAsJsonAsync(
            $"/api/accounts/{account!.Id}/contacts", ContactBody("intruder"));

        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }
}
