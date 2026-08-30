using System.Net;
using System.Net.Http.Headers;
using Aperture.Api.Authentication;
using Aperture.Modules.Access.Authentication;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.Extensions.Logging;

namespace Aperture.Api.Tests;

/// <summary>
/// Every refusal returns the same bare 401, so the only thing that distinguishes them in
/// production is what the host logged. These tests assert the reason, not the status code —
/// a test that only re-checked the 401 would pass against a host that logged nothing, which is
/// the state this portion shipped in until review.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuthenticationLoggingTests(ApiFixture api)
{
    private async Task<HttpStatusCode> CallMeAsync(string token)
    {
        api.Logs.Clear();

        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private CapturedLog SingleDenial(int eventId)
    {
        var denials = api.Logs.Entries
            .Where(e => e.Category == AuthenticationLog.Category && e.EventId == eventId)
            .ToList();

        var denial = Assert.Single(denials);
        Assert.Equal(LogLevel.Warning, denial.Level);
        return denial;
    }

    [Fact]
    public async Task A_token_naming_a_tenant_the_user_has_no_membership_in_logs_that_reason()
    {
        var seeded = await api.SeedAsync("log-no-membership", [Permissions.DealsRead], []);
        var stranger = await api.SeedAsync("log-other-tenant", [], []);

        var status = await CallMeAsync(ApiFixture.CreateToken(stranger.TenantId, seeded.UserId));

        Assert.Equal(HttpStatusCode.Unauthorized, status);

        var denial = SingleDenial(eventId: 1002);
        Assert.Equal(AccessDenialReason.NoActiveMembership, denial.Field("Reason"));

        // The subject and the tenant it reached for, so "one token, many tenant ids" is a
        // pattern somebody can actually query for.
        Assert.Equal(seeded.UserId.Value, denial.Field("Subject"));
        Assert.Equal(stranger.TenantId.Value, denial.Field("TenantId"));
    }

    [Fact]
    public async Task A_deactivated_tenant_logs_a_different_reason()
    {
        var seeded = await api.SeedAsync("log-suspended", [], [], tenantIsActive: false);

        var status = await CallMeAsync(ApiFixture.CreateToken(seeded.TenantId, seeded.UserId));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(AccessDenialReason.TenantInactive, SingleDenial(eventId: 1002).Field("Reason"));
    }

    [Fact]
    public async Task A_deactivated_membership_logs_a_different_reason_again()
    {
        var seeded = await api.SeedAsync("log-revoked", [], [], membershipIsActive: false);

        var status = await CallMeAsync(ApiFixture.CreateToken(seeded.TenantId, seeded.UserId));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(AccessDenialReason.NoActiveMembership, SingleDenial(eventId: 1002).Field("Reason"));
    }

    [Fact]
    public async Task A_token_whose_subject_is_not_an_identifier_logs_that_it_was_malformed()
    {
        var seeded = await api.SeedAsync("log-malformed", [], []);

        var status = await CallMeAsync(ApiFixture.CreateToken(
            seeded.TenantId,
            seeded.UserId,
            extraClaims: [new KeyValuePair<string, object>(AccessClaimTypes.Subject, "not-a-guid")]));

        Assert.Equal(HttpStatusCode.Unauthorized, status);

        SingleDenial(eventId: 1001);

        // Distinct from a resolution failure: a malformed token never reached the database.
        Assert.DoesNotContain(
            api.Logs.Entries,
            e => e.Category == AuthenticationLog.Category && e.EventId == 1002);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_logs_a_validation_rejection()
    {
        var seeded = await api.SeedAsync("log-forged", [], []);

        var status = await CallMeAsync(ApiFixture.CreateToken(
            seeded.TenantId,
            seeded.UserId,
            signingKey: "a-completely-different-key-of-sufficient-length"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);

        var denial = SingleDenial(eventId: 1003);

        // The exception type, never its message — that can carry token contents.
        Assert.Contains("SecurityToken", (string?)denial.Field("FailureType") ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_authentication_logs_no_denial()
    {
        // Guards against the cheapest way to make every test above pass: log a denial always.
        var seeded = await api.SeedAsync("log-quiet", [Permissions.DealsRead], []);

        var status = await CallMeAsync(ApiFixture.CreateToken(seeded.TenantId, seeded.UserId));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(api.Logs.Entries, e => e.Category == AuthenticationLog.Category);
    }

    [Fact]
    public async Task An_unknown_subject_in_a_real_tenant_is_refused_with_a_reason()
    {
        var seeded = await api.SeedAsync("log-ghost-user", [], []);

        var status = await CallMeAsync(ApiFixture.CreateToken(seeded.TenantId, UserId.New()));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(AccessDenialReason.NoActiveMembership, SingleDenial(eventId: 1002).Field("Reason"));
    }
}
