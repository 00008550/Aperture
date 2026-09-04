using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Access.Persistence;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aperture.Api.Tests;

/// <summary>
/// Plan 002-P6 at the HTTP boundary (DOMAIN.md §2 rule 3): this is the portion that turns
/// <c>deals.discount.approve</c> from declared-never-enforced into enforced. Edge 14 — a caller with the
/// permission clears a pending approval (200, and one audit row naming the approver and the why) and the
/// deal may then be won; a caller without it is denied (403). Edge 13's hold surfaces here too: an
/// over-threshold move to won returns 200 with the deal still in negotiation and <c>pendingApproval</c> set.
/// The host runs at the default 20% threshold, so a 30% discount trips the guard without any special config.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DealDiscountApprovalEndpointTests(ApiFixture api)
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

    private static async Task<HttpResponseMessage> TransitionAsync(
        HttpClient client, Guid dealId, string target, string? priceListVersion = null) =>
        await client.PostAsJsonAsync(
            $"/api/deals/{dealId}/transition", new { targetStage = target, priceListVersion });

    /// <summary>Drives a freshly created 30%-discount deal (over the host's 20% threshold) with a priced line
    /// all the way to a pending approval sitting in negotiation, and returns its id.</summary>
    private async Task<Guid> DealPendingApprovalAsync(HttpClient client, string taxId)
    {
        using var accountResp = await client.PostAsJsonAsync("/api/accounts", AccountBody(taxId));
        var account = await accountResp.Content.ReadFromJsonAsync<AccountView>();

        using var create = await client.PostAsJsonAsync(
            "/api/deals",
            new { accountId = account!.Id, name = "over-threshold deal", amount = 5000m, discountPct = 30m });
        var deal = await create.Content.ReadFromJsonAsync<DealView>();

        using var line = await client.PostAsJsonAsync(
            $"/api/deals/{deal!.Id}/lines",
            new { productRef = "SKU-1", unitPrice = 100m, quantity = 2, priceListVersion = "v1" });
        Assert.Equal(HttpStatusCode.OK, line.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(client, deal.Id, "qualified")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(client, deal.Id, "quoted", "v1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(client, deal.Id, "negotiation")).StatusCode);

        using var won = await TransitionAsync(client, deal.Id, "won");
        Assert.Equal(HttpStatusCode.OK, won.StatusCode);
        var held = await won.Content.ReadFromJsonAsync<DealView>();
        Assert.Equal("negotiation", held!.Stage);
        Assert.True(held.PendingApproval);

        return deal.Id;
    }

    // ---- Authorization: the permission is now enforced (edge 14) --------------------------------

    [Fact]
    public async Task An_unauthenticated_approve_is_401()
    {
        using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync(
            $"/api/deals/{Guid.NewGuid()}/approve-discount", new { reason = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_deals_discount_approve_is_denied()
    {
        // An agent who can write deals but was NOT granted the approve permission: the agent alone cannot
        // clear a discount hold (edge 13/14). The policy denies before any deal is even loaded.
        var seeded = await api.SeedAsync(
            "p6-noperm",
            [Permissions.DealsWrite, Permissions.DealsRead],
            [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.PostAsJsonAsync(
            $"/api/deals/{Guid.NewGuid()}/approve-discount", new { reason = "let me in" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_approve_with_no_reason_is_400()
    {
        var seeded = await api.SeedAsync(
            "p6-noreason", [Permissions.DealsDiscountApprove], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await client.PostAsJsonAsync(
            $"/api/deals/{Guid.NewGuid()}/approve-discount", new { reason = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- The full path: hold, approve (audited), then win --------------------------------------

    [Fact]
    public async Task A_lead_with_the_permission_approves_the_hold_which_is_audited_and_the_deal_is_then_won()
    {
        var seeded = await api.SeedAsync(
            "p6-approve",
            [
                Permissions.AccountsWrite, Permissions.DealsWrite, Permissions.DealsRead,
                Permissions.DealsDiscountApprove,
            ],
            [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        var dealId = await DealPendingApprovalAsync(client, "p6-approve");

        using var approve = await client.PostAsJsonAsync(
            $"/api/deals/{dealId}/approve-discount", new { reason = "lead signed off: strategic account" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var cleared = await approve.Content.ReadFromJsonAsync<DealView>();
        Assert.False(cleared!.PendingApproval);

        // The approval is audited: one Mutation row naming the approver, the route, and the why.
        var rows = await MutationRowsAsync(seeded);
        var approvalRow = Assert.Single(rows, r => r.Action == $"POST /api/deals/{dealId}/approve-discount");
        Assert.Equal(ActorKind.Human, approvalRow.ActorKind);
        Assert.Equal("lead signed off: strategic account", approvalRow.Reason);
        Assert.NotNull(approvalRow.CorrelationId);

        // With the approval on record the over-threshold move now wins.
        using var won = await TransitionAsync(client, dealId, "won");
        Assert.Equal(HttpStatusCode.OK, won.StatusCode);
        var deal = await won.Content.ReadFromJsonAsync<DealView>();
        Assert.Equal("won", deal!.Stage);
    }

    [Fact]
    public async Task Approving_a_deal_with_nothing_pending_is_409()
    {
        var seeded = await api.SeedAsync(
            "p6-notpend",
            [
                Permissions.AccountsWrite, Permissions.DealsWrite, Permissions.DealsRead,
                Permissions.DealsDiscountApprove,
            ],
            [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        // A brand-new deal (in `new`, no discount hold) — nothing to approve.
        using var accountResp = await client.PostAsJsonAsync("/api/accounts", AccountBody("p6-notpend"));
        var account = await accountResp.Content.ReadFromJsonAsync<AccountView>();
        using var create = await client.PostAsJsonAsync(
            "/api/deals",
            new { accountId = account!.Id, name = "fresh", amount = 5000m, discountPct = 5m });
        var deal = await create.Content.ReadFromJsonAsync<DealView>();

        using var response = await client.PostAsJsonAsync(
            $"/api/deals/{deal!.Id}/approve-discount", new { reason = "nothing here" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private async Task<IReadOnlyList<AuditEvent>> MutationRowsAsync(SeededPrincipal principal)
    {
        using var scope = api.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
        using (AmbientTenantContext.Begin(principal.TenantId))
        {
            return await db.AuditEvents
                .Where(e => e.ActorUserId == principal.UserId && e.Category == AuditCategory.Mutation)
                .ToListAsync();
        }
    }
}
