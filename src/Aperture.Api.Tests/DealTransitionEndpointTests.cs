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
/// Plan 002-P5 at the HTTP boundary: the transition route enforces <c>deals.write</c> (edge 17), the state
/// machine's verdicts surface as the right status codes (200 on a legal move, 422 on an illegal edge or a
/// failed rule guard, 404 for a deal the caller cannot see), and — the half that only exists once the Access
/// trail is composed alongside Sales — every transition writes one audit row naming the actor, the from → to
/// stages and the reason. The rule and concurrency mechanics are proven at the service level in the Sales
/// test project; this pins the wire contract and the audit record.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DealTransitionEndpointTests(ApiFixture api)
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

    private async Task<(HttpClient Client, SeededPrincipal Principal, DealView Deal)> DealInNewAsync(string taxId)
    {
        var seeded = await api.SeedAsync(
            taxId,
            [Permissions.AccountsWrite, Permissions.DealsWrite, Permissions.DealsRead],
            [(ScopeGrantKind.Self, null)]);
        var client = Client(seeded);

        using var accountResp = await client.PostAsJsonAsync("/api/accounts", AccountBody(taxId));
        var account = await accountResp.Content.ReadFromJsonAsync<AccountView>();

        using var create = await client.PostAsJsonAsync(
            "/api/deals", new { accountId = account!.Id, name = "wire deal", amount = 5000m, discountPct = 5m });
        var deal = await create.Content.ReadFromJsonAsync<DealView>();

        return (client, seeded, deal!);
    }

    private static async Task<HttpResponseMessage> TransitionAsync(
        HttpClient client, Guid dealId, string target, string? reason = null, string? priceListVersion = null) =>
        await client.PostAsJsonAsync(
            $"/api/deals/{dealId}/transition",
            new { targetStage = target, reason, priceListVersion });

    // ---- Authorization (edge 17) --------------------------------------------------------------

    [Fact]
    public async Task An_unauthenticated_transition_is_401()
    {
        using var client = api.CreateClient();
        using var response = await TransitionAsync(client, Guid.NewGuid(), "qualified");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_deals_write_cannot_transition()
    {
        var seeded = await api.SeedAsync(
            "t-read-only", [Permissions.DealsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await TransitionAsync(client, Guid.NewGuid(), "qualified");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Status-code contract -----------------------------------------------------------------

    [Fact]
    public async Task A_legal_transition_returns_200_with_the_advanced_deal()
    {
        var (client, _, deal) = await DealInNewAsync("t-legal");
        using var _c = client;

        using var response = await TransitionAsync(client, deal.Id, "qualified");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var advanced = await response.Content.ReadFromJsonAsync<DealView>();
        Assert.Equal("qualified", advanced!.Stage);
    }

    [Fact]
    public async Task An_illegal_transition_returns_422()
    {
        var (client, _, deal) = await DealInNewAsync("t-illegal");
        using var _c = client;

        // new -> won is not an edge.
        using var response = await TransitionAsync(client, deal.Id, "won");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_transition_on_a_deal_the_caller_cannot_see_is_404()
    {
        var seeded = await api.SeedAsync(
            "t-notfound", [Permissions.DealsWrite], [(ScopeGrantKind.Self, null)]);
        using var client = Client(seeded);

        using var response = await TransitionAsync(client, Guid.NewGuid(), "qualified");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- The audit row (from -> to + reason) --------------------------------------------------

    [Fact]
    public async Task A_lost_transition_writes_an_audit_row_with_from_to_and_the_reason()
    {
        var (client, principal, deal) = await DealInNewAsync("t-audit");
        using var _c = client;

        // Drive to negotiation, then lose it with a reason.
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(client, deal.Id, "qualified")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await TransitionAsync(client, deal.Id, "quoted", priceListVersion: "v1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(client, deal.Id, "negotiation")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await TransitionAsync(client, deal.Id, "lost", reason: "competitor-cheaper")).StatusCode);

        var rows = await MutationRowsAsync(principal);

        // Every one of the four transitions is audited; the lost one carries the reason and its from -> to.
        Assert.Equal(4, rows.Count);
        var lostRow = Assert.Single(rows, r => r.Action == $"POST /api/deals/{deal.Id}/transition negotiation->lost");
        Assert.Equal(ActorKind.Human, lostRow.ActorKind);
        Assert.Equal("competitor-cheaper", lostRow.Reason);
        Assert.NotNull(lostRow.CorrelationId);

        // And the very first move recorded new -> qualified.
        Assert.Contains(rows, r => r.Action == $"POST /api/deals/{deal.Id}/transition new->qualified");
    }

    [Fact]
    public async Task An_illegal_transition_attempt_is_still_audited()
    {
        var (client, principal, deal) = await DealInNewAsync("t-audit-bad");
        using var _c = client;

        using var response = await TransitionAsync(client, deal.Id, "won");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var rows = await MutationRowsAsync(principal);
        var row = Assert.Single(rows);
        Assert.Equal($"POST /api/deals/{deal.Id}/transition new->won", row.Action);
        Assert.Equal("rejected: IllegalTransition", row.Reason);
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
