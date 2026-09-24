using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;

namespace Aperture.Api.Tests;

/// <summary>
/// Plan 011-P2, edge 11, over the real host: a list cursor is caller input, so every way it can fail to be
/// one the server minted is a <c>400</c> naming <c>cursor</c> on all three grids — through the same
/// <c>ApiExceptionHandler</c> as P1's validation, never a 500 and never an internal detail. An empty cursor is
/// the first page and a real <c>nextCursor</c> still pages. Also the P2 added item: the status stays correct
/// when the caller's <c>Accept</c> excludes JSON.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CursorValidationEndpointTests(ApiFixture api)
{
    public static readonly TheoryData<string> Grids = new() { "/api/accounts", "/api/contacts", "/api/deals" };

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>Edge 11's five malformed shapes (plus a truncated real-looking cursor), × each grid.</summary>
    public static TheoryData<string, string> MalformedCursors()
    {
        var guid = Guid.NewGuid().ToString("D");
        var realLooking = B64($"{DateTimeOffset.UtcNow.UtcTicks:D}:{guid}");
        string[] cursors =
        [
            "%%%",                                   // not base64
            B64("abc"),                              // no separator
            B64($"99999999999999999999:{guid}"),     // ticks overflow a long
            B64("1:not-a-guid"),                     // id is not a guid
            B64($"-1:{guid}"),                       // ticks out of DateTimeOffset's range
            B64($"{long.MaxValue:D}:{guid}"),        // fits a long, still beyond DateTimeOffset.MaxValue
            realLooking[..(realLooking.Length - 5)], // truncated: invalid base64 length
            realLooking[..20],                       // truncated on a 4-char boundary: valid base64, garbage payload
        ];

        var data = new TheoryData<string, string>();
        foreach (var grid in new[] { "/api/accounts", "/api/contacts", "/api/deals" })
        {
            foreach (var cursor in cursors)
            {
                data.Add(grid, cursor);
            }
        }

        return data;
    }

    private HttpClient Client(SeededPrincipal principal)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.CreateToken(principal.TenantId, principal.UserId));
        return client;
    }

    private Task<SeededPrincipal> Seed(string label) => api.SeedAsync(
        label,
        [Permissions.AccountsRead, Permissions.AccountsWrite, Permissions.ContactsRead, Permissions.ContactsWrite,
         Permissions.DealsRead, Permissions.DealsWrite],
        [(ScopeGrantKind.Self, null)]);

    private static void AssertNothingLeaks(string raw)
    {
        Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Aperture.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:line", raw, StringComparison.Ordinal);
    }

    private static async Task AssertCursorProblemAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {raw}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertNothingLeaks(raw);

        using var json = JsonDocument.Parse(raw);
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.True(json.RootElement.TryGetProperty("traceId", out _), raw);
        var errors = json.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("cursor", out var messages), $"errors.cursor missing in {raw}");
        Assert.Contains(messages.EnumerateArray(), m => !string.IsNullOrWhiteSpace(m.GetString()));
    }

    [Theory]
    [MemberData(nameof(MalformedCursors))]
    public async Task A_malformed_cursor_is_400_errors_cursor_on_every_grid(string grid, string cursor)
    {
        using var client = Client(await Seed("cur-bad"));

        using var response = await client.GetAsync(
            new Uri($"{grid}?cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative));

        await AssertCursorProblemAsync(response);
    }

    [Theory]
    [MemberData(nameof(Grids))]
    public async Task An_empty_cursor_is_the_first_page(string grid)
    {
        using var client = Client(await Seed("cur-empty"));

        using var withEmpty = await client.GetAsync(new Uri($"{grid}?cursor=", UriKind.Relative));
        using var without = await client.GetAsync(new Uri(grid, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, withEmpty.StatusCode);
        Assert.Equal(await without.Content.ReadAsStringAsync(), await withEmpty.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_real_nextCursor_round_trips_on_every_grid()
    {
        using var client = Client(await Seed("cur-page"));
        for (var i = 0; i < 3; i++)
        {
            using var created = await client.PostAsJsonAsync("/api/accounts", new
            {
                name = $"Paged {i}",
                taxId = $"TX-C-{Guid.NewGuid():N}"[..20],
                creditLimit = 1000m,
                paymentTermsDays = 30,
                regionId = (Guid?)null,
                teamId = (Guid?)null,
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var account = (await created.Content.ReadFromJsonAsync<AccountView>())!;

            using var contact = await client.PostAsJsonAsync(
                $"/api/accounts/{account.Id}/contacts",
                new { name = $"Contact {i}", email = (string?)null, phone = (string?)null, messenger = (string?)null });
            Assert.Equal(HttpStatusCode.Created, contact.StatusCode);

            using var deal = await client.PostAsJsonAsync(
                "/api/deals", new { accountId = account.Id, name = $"Deal {i}", amount = 10m, discountPct = 0m });
            Assert.Equal(HttpStatusCode.Created, deal.StatusCode);
        }

        await AssertPagesAsync<AccountsPage>(client, "/api/accounts", p => p.Items.Select(a => a.Id), p => p.NextCursor);
        await AssertPagesAsync<ContactsPage>(client, "/api/contacts", p => p.Items.Select(c => c.Id), p => p.NextCursor);
        await AssertPagesAsync<DealsPage>(client, "/api/deals", p => p.Items.Select(d => d.Id), p => p.NextCursor);
    }

    private static async Task AssertPagesAsync<TPage>(
        HttpClient client, string grid, Func<TPage, IEnumerable<Guid>> ids, Func<TPage, string?> next)
    {
        var all = ids((await client.GetFromJsonAsync<TPage>(grid))!).ToList();
        Assert.Equal(3, all.Count);

        var first = (await client.GetFromJsonAsync<TPage>($"{grid}?limit=2"))!;
        var cursor = next(first);
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = (await client.GetFromJsonAsync<TPage>($"{grid}?limit=2&cursor={Uri.EscapeDataString(cursor!)}"))!;
        Assert.Null(next(second));
        Assert.Equal(all, ids(first).Concat(ids(second)).ToList());
    }

    // ---- Added item (2026-09-24): the status survives an Accept header that excludes JSON --------------

    [Fact]
    public async Task A_malformed_cursor_with_Accept_text_html_is_still_400_problem_json()
    {
        using var client = Client(await Seed("cur-html"));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/deals?cursor=not-a-cursor", UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/html");

        using var response = await client.SendAsync(request);

        await AssertCursorProblemAsync(response);
    }

    [Fact]
    public async Task A_validation_error_with_Accept_text_html_is_still_400_naming_the_field()
    {
        using var client = Client(await Seed("val-html"));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/accounts", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                name = " ",
                taxId = $"TX-H-{Guid.NewGuid():N}"[..20],
                creditLimit = 1000m,
                paymentTermsDays = 30,
                regionId = (Guid?)null,
                teamId = (Guid?)null,
            }),
        };
        request.Headers.Accept.ParseAdd("text/html");

        using var response = await client.SendAsync(request);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {raw}");
        AssertNothingLeaks(raw);
        using var json = JsonDocument.Parse(raw);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("name", out _), raw);
    }
}
