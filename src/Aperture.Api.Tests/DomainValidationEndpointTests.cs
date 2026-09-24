using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Aperture.Api.Errors;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aperture.Api.Tests;

/// <summary>
/// Plan 011-P1, edges 1–9, over the real host: an aggregate's input rule broken through any Sales route is a
/// <c>400</c> <c>application/problem+json</c> naming the field (never a 500, never a re-validation at the
/// endpoint), nothing about the server leaks into any error body, a genuine fault is an opaque 500 with a
/// <c>traceId</c>, and authorization is decided before the body is ever looked at.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DomainValidationEndpointTests(ApiFixture api)
{
    private HttpClient Client(SeededPrincipal principal, HttpClient? client = null)
    {
        client ??= api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(principal.TenantId, principal.UserId));
        return client;
    }

    private Task<SeededPrincipal> Writer(string label) => api.SeedAsync(
        label,
        [Permissions.AccountsRead, Permissions.AccountsWrite, Permissions.ContactsWrite,
         Permissions.DealsRead, Permissions.DealsWrite],
        [(ScopeGrantKind.Self, null)]);

    private static object AccountBody(
        string taxId, string name = "Acme", decimal creditLimit = 1000m, int paymentTermsDays = 30) => new
    {
        name,
        taxId,
        creditLimit,
        paymentTermsDays,
        regionId = (Guid?)null,
        teamId = (Guid?)null,
    };

    private static object DealBody(Guid accountId, string name = "deal", decimal amount = 100m, decimal discountPct = 0m) =>
        new { accountId, name, amount, discountPct };

    private static object LineBody(string productRef = "SKU", decimal unitPrice = 10m, int quantity = 1) =>
        new { productRef, unitPrice, quantity, priceListVersion = (string?)null };

    private static async Task<AccountView> CreateAccountAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/accounts", AccountBody($"TX-V-{Guid.NewGuid():N}"[..20]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountView>())!;
    }

    private static async Task<DealView> CreateDealAsync(HttpClient client, Guid accountId)
    {
        using var response = await client.PostAsJsonAsync("/api/deals", DealBody(accountId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DealView>())!;
    }

    /// <summary>
    /// Edge 7 on every 400: problem+json, <c>errors.&lt;field&gt;</c> present, and nothing that names the
    /// server's internals — no exception type, no namespace, no stack frame.
    /// </summary>
    private static async Task AssertValidationProblemAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var raw = await response.Content.ReadAsStringAsync();
        AssertNothingLeaks(raw);

        using var json = JsonDocument.Parse(raw);
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.True(json.RootElement.TryGetProperty("traceId", out _), raw);
        var errors = json.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out var messages), $"errors.{field} missing in {raw}");
        Assert.Contains(messages.EnumerateArray(), m => !string.IsNullOrWhiteSpace(m.GetString()));
    }

    private static void AssertNothingLeaks(string raw)
    {
        Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Aperture.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:line", raw, StringComparison.Ordinal);
    }

    // ---- Edge 1–3: deal create ------------------------------------------------------------------

    [Fact]
    public async Task Blank_deal_name_is_400_errors_name_and_no_deal_is_written()
    {
        using var client = Client(await Writer("v-dname"));
        var account = await CreateAccountAsync(client);

        using var response = await client.PostAsJsonAsync("/api/deals", DealBody(account.Id, name: "  "));

        await AssertValidationProblemAsync(response, "name");
        using var list = await client.GetAsync(new Uri("/api/deals", UriKind.Relative));
        var page = await list.Content.ReadFromJsonAsync<DealsPage>();
        Assert.DoesNotContain(page!.Items, d => d.AccountId == account.Id);
    }

    [Fact]
    public async Task Negative_deal_amount_is_400_errors_amount()
    {
        using var client = Client(await Writer("v-damt"));
        var account = await CreateAccountAsync(client);

        using var response = await client.PostAsJsonAsync("/api/deals", DealBody(account.Id, amount: -1m));

        await AssertValidationProblemAsync(response, "amount");
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    public async Task Deal_discount_outside_0_to_100_is_400_errors_discountPct(string discount)
    {
        using var client = Client(await Writer("v-ddisc"));
        var account = await CreateAccountAsync(client);

        using var response = await client.PostAsJsonAsync(
            "/api/deals",
            DealBody(account.Id, discountPct: decimal.Parse(discount, System.Globalization.CultureInfo.InvariantCulture)));

        await AssertValidationProblemAsync(response, "discountPct");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Deal_discount_bounds_0_and_100_are_inclusive_and_created(int discount)
    {
        using var client = Client(await Writer("v-dbound"));
        var account = await CreateAccountAsync(client);

        using var response = await client.PostAsJsonAsync("/api/deals", DealBody(account.Id, discountPct: discount));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var deal = await response.Content.ReadFromJsonAsync<DealView>();
        Assert.Equal(discount, deal!.DiscountPct);
    }

    // ---- Edge 4: account create and PATCH -------------------------------------------------------

    [Theory]
    [InlineData(" ", 1000, 30, "name")]
    [InlineData("Acme", -1, 30, "creditLimit")]
    [InlineData("Acme", 1000, -1, "paymentTermsDays")]
    public async Task Account_create_guards_are_400_naming_the_field(
        string name, int creditLimit, int paymentTermsDays, string field)
    {
        using var client = Client(await Writer("v-acre"));

        using var response = await client.PostAsJsonAsync(
            "/api/accounts", AccountBody($"TX-V-{Guid.NewGuid():N}"[..20], name, creditLimit, paymentTermsDays));

        await AssertValidationProblemAsync(response, field);
    }

    [Fact]
    public async Task Account_create_with_a_blank_tax_id_is_400_errors_taxId()
    {
        using var client = Client(await Writer("v-atax"));

        using var response = await client.PostAsJsonAsync("/api/accounts", AccountBody(" "));

        await AssertValidationProblemAsync(response, "taxId");
    }

    [Theory]
    [InlineData(" ", 1000, 30, "name")]
    [InlineData("Acme", -1, 30, "creditLimit")]
    [InlineData("Acme", 1000, -1, "paymentTermsDays")]
    public async Task Account_patch_guards_are_400_and_leave_the_row_and_its_version_unchanged(
        string name, int creditLimit, int paymentTermsDays, string field)
    {
        var seeded = await Writer("v-apatch");
        using var client = Client(seeded);
        var before = await CreateAccountAsync(client);

        using var response = await client.PatchAsJsonAsync($"/api/accounts/{before.Id}", new
        {
            ownerUserId = seeded.UserId.Value,
            name,
            creditLimit,
            paymentTermsDays,
            regionId = (Guid?)null,
            teamId = (Guid?)null,
            expectedVersion = before.Version,
        });

        await AssertValidationProblemAsync(response, field);

        using var get = await client.GetAsync(new Uri($"/api/accounts/{before.Id}", UriKind.Relative));
        var after = await get.Content.ReadFromJsonAsync<AccountView>();
        // Field by field: the create response carries CreatedAt at .NET tick precision, the read-back at
        // PostgreSQL's microseconds, so whole-record equality would compare the clock, not the row.
        Assert.Equal(before.Version, after!.Version);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.CreditLimit, after.CreditLimit);
        Assert.Equal(before.PaymentTermsDays, after.PaymentTermsDays);
        Assert.Equal(before.OwnerUserId, after.OwnerUserId);
    }

    // ---- Edge 5: contact ------------------------------------------------------------------------

    [Fact]
    public async Task Blank_contact_name_is_400_errors_name()
    {
        using var client = Client(await Writer("v-cname"));
        var account = await CreateAccountAsync(client);

        using var response = await client.PostAsJsonAsync(
            $"/api/accounts/{account.Id}/contacts",
            new { name = " ", email = (string?)null, phone = (string?)null, messenger = (string?)null });

        await AssertValidationProblemAsync(response, "name");
    }

    // ---- Edge 6: deal lines ---------------------------------------------------------------------

    [Theory]
    [InlineData(" ", 10, 1, "productRef")]
    [InlineData("SKU", -1, 1, "unitPrice")]
    [InlineData("SKU", 10, 0, "quantity")]
    [InlineData("SKU", 10, -3, "quantity")]
    public async Task Line_guards_are_400_naming_the_field_and_add_no_line(
        string productRef, int unitPrice, int quantity, string field)
    {
        using var client = Client(await Writer("v-line"));
        var deal = await CreateDealAsync(client, (await CreateAccountAsync(client)).Id);

        using var response = await client.PostAsJsonAsync(
            $"/api/deals/{deal.Id}/lines", LineBody(productRef, unitPrice, quantity));

        await AssertValidationProblemAsync(response, field);
        using var get = await client.GetAsync(new Uri($"/api/deals/{deal.Id}", UriKind.Relative));
        Assert.Empty((await get.Content.ReadFromJsonAsync<DealView>())!.Lines);
    }

    [Fact]
    public async Task Line_with_zero_unit_price_and_quantity_one_is_accepted()
    {
        using var client = Client(await Writer("v-lineok"));
        var deal = await CreateDealAsync(client, (await CreateAccountAsync(client)).Id);

        using var response = await client.PostAsJsonAsync($"/api/deals/{deal.Id}/lines", LineBody(unitPrice: 0m, quantity: 1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Edge 8: a genuine fault is an opaque 500 -------------------------------------------------

    private const string SecretMessage = "npgsql pool exhausted on host db-internal-7 (secret detail)";

    [Fact]
    public async Task An_unexpected_exception_is_an_opaque_500_problem_with_a_traceId()
    {
        var seeded = await Writer("v-fault");
        await using var faulty = api.Factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddScoped<IDealService>(_ => ThrowingProxy<IDealService>.Create())));
        using var client = Client(seeded, faulty.CreateClient());
        api.Logs.Clear();

        using var response = await client.GetAsync(new Uri("/api/deals", UriKind.Relative));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret detail", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("db-internal-7", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperation", raw, StringComparison.Ordinal);
        AssertNothingLeaks(raw);

        using var json = JsonDocument.Parse(raw);
        Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", json.RootElement.GetProperty("title").GetString());
        var traceId = json.RootElement.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.False(json.RootElement.TryGetProperty("detail", out _), raw);

        // The detail is not lost — it is in the log, at Error, where an operator can join it by trace.
        Assert.Contains(api.Logs.Entries, e =>
            e.Category == typeof(ApiExceptionHandler).FullName && e.Level == LogLevel.Error);
    }

    // ---- Edge 9: authorization precedes validation ------------------------------------------------

    [Fact]
    public async Task A_caller_without_deals_write_posting_an_invalid_deal_gets_403_not_400()
    {
        var reader = await api.SeedAsync("v-noauth", [Permissions.DealsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(reader);

        using var response = await client.PostAsJsonAsync(
            "/api/deals", DealBody(Guid.NewGuid(), name: " ", amount: -1m, discountPct: 101m));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("errors", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_without_deals_write_adding_an_invalid_line_gets_403_not_400()
    {
        var reader = await api.SeedAsync("v-noline", [Permissions.DealsRead], [(ScopeGrantKind.Self, null)]);
        using var client = Client(reader);

        using var response = await client.PostAsJsonAsync(
            $"/api/deals/{Guid.NewGuid()}/lines", LineBody(productRef: " ", unitPrice: -1m, quantity: 0));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Fault injection: every member throws, carrying a message that must never reach the wire.</summary>
    public class ThrowingProxy<T> : DispatchProxy
        where T : class
    {
        public static T Create() => DispatchProxy.Create<T, ThrowingProxy<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(SecretMessage);
    }
}
