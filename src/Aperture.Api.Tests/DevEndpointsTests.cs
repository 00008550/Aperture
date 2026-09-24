using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aperture.Api.Development;
using Aperture.Api.Endpoints;
using Aperture.Modules.Access.Persistence;
using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;

namespace Aperture.Api.Tests;

/// <summary>
/// 010-P5a: the Development-only sign-in affordances. The Development gate is the whole safety argument,
/// so the Production/Staging/Testing 404s are the tests that matter most here.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DevEndpointsTests(ApiFixture api)
{
    // Derived from the fixture's factory, so it inherits the container connection strings, the
    // signing settings, and the log capture — only the environment differs.
    private WebApplicationFactory<Program> Host(string environment) =>
        api.Factory.WithWebHostBuilder(host => host.UseEnvironment(environment));

    private WebApplicationFactory<Program> DevelopmentHost()
    {
        var factory = Host(Environments.Development);

        // The Development appsettings name aperture_dev on localhost. A test that silently used them
        // would write into the developer's database; prove the container connection won.
        using var scope = factory.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<AccessDbContext>().Database.GetConnectionString();
        Assert.Equal(api.ConnectionString, connection);

        return factory;
    }

    private static HttpRequestMessage Me(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> MintAsync(HttpClient client, DevTokenRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/dev/token", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DevTokenResponse>();
        Assert.NotNull(body);
        return body.AccessToken;
    }

    [Fact]
    public async Task A_mint_for_an_active_member_returns_a_token_api_me_accepts_with_the_members_real_permissions()
    {
        var seeded = await api.SeedAsync("devmint", [Permissions.AccountsRead, Permissions.DealsRead], []);
        using var client = DevelopmentHost().CreateClient();

        var token = await MintAsync(client, new DevTokenRequest(null, seeded.TenantId.Value, null, seeded.UserId.Value));

        using var me = await client.SendAsync(Me(token));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var session = await me.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(session);
        Assert.Equal(seeded.TenantId.Value, session.TenantId);
        Assert.Equal(seeded.UserId.Value, session.UserId);
        Assert.Equal([Permissions.AccountsRead, Permissions.DealsRead], session.Permissions);
    }

    [Fact]
    public async Task A_mint_resolves_tenant_slug_and_user_email_and_logs_ids_but_never_the_token()
    {
        var seeded = await api.SeedAsync("devslug", [Permissions.AccountsRead], []);
        var factory = DevelopmentHost();

        string slug;
        string email;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            slug = (await db.Tenants.SingleAsync(t => t.Id == seeded.TenantId)).Slug;
            email = (await db.Users.SingleAsync(u => u.Id == seeded.UserId)).Email;
        }

        using var client = factory.CreateClient();
        var token = await MintAsync(client, new DevTokenRequest(slug, null, email.ToUpperInvariant(), null));

        using var me = await client.SendAsync(Me(token));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var minted = api.Logs.Entries.Where(e => e.EventId == 1004).ToList();
        Assert.Contains(minted, e => Equals(e.Field("Subject"), seeded.UserId.Value)
                                     && Equals(e.Field("TenantId"), seeded.TenantId.Value));
        Assert.DoesNotContain(api.Logs.Entries, e => e.Message.Contains(token, StringComparison.Ordinal));
    }

    public static TheoryData<string> RefusedCases => ["non-member", "deactivated-membership", "unknown-tenant", "unknown-user", "empty-body"];

    [Theory]
    [MemberData(nameof(RefusedCases))]
    public async Task A_mint_that_cannot_resolve_an_active_membership_is_a_uniform_404(string refusal)
    {
        var member = await api.SeedAsync("devok", [Permissions.AccountsRead], []);
        var other = await api.SeedAsync("devother", [Permissions.AccountsRead], []);
        var inactive = await api.SeedAsync("devoff", [Permissions.AccountsRead], [], membershipIsActive: false);

        DevTokenRequest? request = refusal switch
        {
            "non-member" => new(null, other.TenantId.Value, null, member.UserId.Value),
            "deactivated-membership" => new(null, inactive.TenantId.Value, null, inactive.UserId.Value),
            "unknown-tenant" => new("no-such-tenant", null, null, member.UserId.Value),
            "unknown-user" => new(null, member.TenantId.Value, "nobody@example.com", null),
            _ => null,
        };

        using var client = DevelopmentHost().CreateClient();
        using var response = request is null
            ? await client.PostAsync("/api/dev/token", JsonContent.Create<DevTokenRequest?>(null))
            : await client.PostAsJsonAsync("/api/dev/token", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Uniform: nothing in the body says which half failed.
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_minted_token_carries_identity_only_and_no_perm_claim()
    {
        var seeded = await api.SeedAsync("devperm", [Permissions.AccountsRead, Permissions.AdminUsers], []);
        using var client = DevelopmentHost().CreateClient();

        var token = new JsonWebToken(
            await MintAsync(client, new DevTokenRequest(null, seeded.TenantId.Value, null, seeded.UserId.Value)));

        Assert.DoesNotContain(token.Claims, c => c.Type == "perm");
        Assert.Equal(seeded.UserId.Value.ToString(), token.GetClaim("sub").Value);
        Assert.Equal(seeded.TenantId.Value.ToString(), token.GetClaim("tenant_id").Value);
        Assert.True(token.ValidTo <= DateTime.UtcNow.Add(DevEndpoints.TokenLifetime).AddMinutes(1));
    }

    [Fact]
    public async Task Revoking_the_membership_after_the_mint_makes_api_me_deny()
    {
        var seeded = await api.SeedAsync("devrevoke", [Permissions.AccountsRead], []);
        var factory = DevelopmentHost();
        using var client = factory.CreateClient();
        var token = await MintAsync(client, new DevTokenRequest(null, seeded.TenantId.Value, null, seeded.UserId.Value));

        using (var before = await client.SendAsync(Me(token)))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            var membership = await db.Memberships.IgnoreQueryFilters().SingleAsync(m => m.Id == seeded.MembershipId);
            db.Entry(membership).Property(m => m.IsActive).CurrentValue = false;
            await db.SaveChangesAsync();
        }

        using var after = await client.SendAsync(Me(token));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task Outside_Development_both_dev_routes_do_not_exist(string environment)
    {
        var seeded = await api.SeedAsync("devprod", [Permissions.AccountsRead], []);
        using var client = Host(environment).CreateClient();
        var body = new DevTokenRequest(null, seeded.TenantId.Value, null, seeded.UserId.Value);

        // Authenticated, so the host's fallback policy (which answers 401 to an anonymous request for
        // ANY unmatched path) is out of the way: what remains is the router, and it has no such route.
        var bearer = new AuthenticationHeaderValue("Bearer", ApiFixture.CreateToken(seeded.TenantId, seeded.UserId));
        using var usersRequest = new HttpRequestMessage(HttpMethod.Get, "/api/dev/users") { Headers = { Authorization = bearer } };
        using var mintRequest = new HttpRequestMessage(HttpMethod.Post, "/api/dev/token")
        {
            Headers = { Authorization = bearer },
            Content = JsonContent.Create(body),
        };
        using var users = await client.SendAsync(usersRequest);
        using var mint = await client.SendAsync(mintRequest);

        Assert.Equal(HttpStatusCode.NotFound, users.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, mint.StatusCode);

        // And anonymously — the only way the route is meant to be called — nothing is minted either.
        using var anonymous = await client.PostAsJsonAsync("/api/dev/token", body);
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.DoesNotContain("accessToken", await anonymous.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Absent, not merely denied: no endpoint with either route exists in the built host.
        var routes = Host(environment).Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();
        Assert.DoesNotContain("/api/dev/users", routes);
        Assert.DoesNotContain("/api/dev/token", routes);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Seed_demo_outside_Development_is_refused_before_any_service_exists(string environment)
    {
        // Decide takes no service provider and no connection: a refusal cannot have touched a database.
        var decision = DemoSeedCommand.Decide(
            ["--seed-demo"], new HostingEnvironment { EnvironmentName = environment }, NullLogger.Instance);

        Assert.Equal(DemoSeedDecision.Refuse, decision);
    }

    [Fact]
    public void Seed_demo_is_honoured_only_in_Development_and_ignored_without_the_flag()
    {
        var development = new HostingEnvironment { EnvironmentName = Environments.Development };

        Assert.Equal(DemoSeedDecision.Seed, DemoSeedCommand.Decide(["--seed-demo"], development, NullLogger.Instance));
        Assert.Equal(DemoSeedDecision.Serve, DemoSeedCommand.Decide(["--urls", "x"], development, NullLogger.Instance));
        Assert.Equal(["--urls", "x"], DemoSeedCommand.HostArgs(["--urls", "--seed-demo", "x"]));
    }

    [Fact]
    public async Task Running_the_seed_twice_yields_identical_row_counts_and_lists_the_five_demo_users()
    {
        // A separate aperture_dev database on the test container — the seed writes only to that name.
        var devConnection = new NpgsqlConnectionStringBuilder(api.ConnectionString) { Database = DemoSeed.DatabaseName };
        var readerConnection = new NpgsqlConnectionStringBuilder(devConnection.ConnectionString)
        {
            Username = "aperture_reader",
            Password = null,
        };

        var factory = api.Factory.WithWebHostBuilder(host =>
        {
            host.UseEnvironment(Environments.Development);
            host.UseSetting("ConnectionStrings:Aperture", devConnection.ConnectionString);
            host.UseSetting("ConnectionStrings:ApertureReader", readerConnection.ConnectionString);
            host.UseSetting("Aperture:ReaderPassword", "aperture_reader");
        });

        await RunSeedAsync(factory);
        var first = await CountRowsAsync(factory);
        await RunSeedAsync(factory);
        var second = await CountRowsAsync(factory);

        Assert.Equal(first, second);
        Assert.Equal(5, first["users"]);
        Assert.Equal(1, first["tenants"]);
        Assert.True(first["accounts"] > 0 && first["contacts"] > 0 && first["deals"] > 0 && first["lines"] > 0);

        using var client = factory.CreateClient();
        var listed = await client.GetFromJsonAsync<DevUsersResponse>("/api/dev/users");
        Assert.NotNull(listed);
        Assert.Equal(DemoSeed.TenantSlug, listed.TenantSlug);
        Assert.Equal(DemoSeed.Personas.Select(p => p.Email), listed.Users.Select(u => u.Email));

        // Every persona can sign in; the no-scope one resolves with an empty scope set, not a widened one.
        foreach (var persona in DemoSeed.Personas)
        {
            var token = await MintAsync(client, new DevTokenRequest(DemoSeed.TenantSlug, null, persona.Email, null));
            using var me = await client.SendAsync(Me(token));
            var session = await me.Content.ReadFromJsonAsync<MeResponse>();
            Assert.NotNull(session);
            Assert.Equal(persona.Grants.Count, session.Scopes.Count);
        }
    }

    private static async Task RunSeedAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DemoSeed>().RunAsync();
    }

    private static async Task<Dictionary<string, int>> CountRowsAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
        var sales = scope.ServiceProvider.GetRequiredService<SalesDbContext>();

        return new Dictionary<string, int>
        {
            ["tenants"] = await access.Tenants.CountAsync(),
            ["users"] = await access.Users.CountAsync(),
            ["memberships"] = await access.Memberships.IgnoreQueryFilters().CountAsync(),
            ["roles"] = await access.Roles.IgnoreQueryFilters().CountAsync(),
            ["role_permissions"] = await access.RolePermissions.IgnoreQueryFilters().CountAsync(),
            ["membership_roles"] = await access.MembershipRoles.IgnoreQueryFilters().CountAsync(),
            ["scope_grants"] = await access.ScopeGrants.IgnoreQueryFilters().CountAsync(),
            ["regions"] = await access.Regions.IgnoreQueryFilters().CountAsync(),
            ["accounts"] = await sales.Accounts.IgnoreQueryFilters().CountAsync(),
            ["contacts"] = await sales.Contacts.IgnoreQueryFilters().CountAsync(),
            ["deals"] = await sales.Deals.IgnoreQueryFilters().CountAsync(),
            ["lines"] = await sales.DealLines.IgnoreQueryFilters().CountAsync(),
        };
    }
}
