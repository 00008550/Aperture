using System.Text;
using Aperture.Modules.Access.Domain;
using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace Aperture.Api.Tests;

/// <summary>
/// The real API host, over a real PostgreSQL with the real migration applied.
/// <para>
/// The thing under test is whether a token becomes a principal, and that answer comes out of
/// the database. A stubbed resolver would test the stub: it is precisely the "does this user
/// still belong to this tenant" query that P3 exists to make load-bearing.
/// </para>
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string Issuer = "https://tests.aperture/";
    public const string Audience = "aperture-tests";
    public const string SigningKey = "test-signing-key-at-least-thirty-two-bytes-long";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("aperture")
        .WithUsername("aperture")
        .WithPassword("aperture")
        .Build();

    private WebApplicationFactory<Program>? _factory;

    public WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("The fixture has not been initialised.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            // "Testing", not "Development": the development appsettings carries a signing key,
            // and a test that silently inherited it would still pass if this configuration
            // stopped being applied.
            host.UseEnvironment("Testing");

            // UseSetting, not ConfigureAppConfiguration. Program.cs reads its configuration on
            // the WebApplicationBuilder, before the deferred host builder runs its
            // ConfigureAppConfiguration callbacks, so those callbacks arrive too late and the
            // host starts unconfigured. UseSetting lands in host configuration, which is
            // already there.
            host.UseSetting("ConnectionStrings:Aperture", _container.GetConnectionString());
            host.UseSetting("Authentication:Issuer", Issuer);
            host.UseSetting("Authentication:Audience", Audience);
            host.UseSetting("Authentication:SigningKey", SigningKey);
        });

        // Migrate through the host's own registration, so the test also proves the API
        // composes the module's DbContext correctly.
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AccessDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    public HttpClient CreateClient() => Factory.CreateClient();

    /// <summary>
    /// Mints a token the API will accept structurally. Whether it authenticates is then purely
    /// a question of what the database says — which is the point of every test here.
    /// </summary>
    public static string CreateToken(
        TenantId tenantId,
        UserId userId,
        string? signingKey = null,
        string? issuer = null,
        DateTime? expires = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey ?? SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = Audience,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.Value.ToString(),
                ["tenant_id"] = tenantId.Value.ToString(),
            },
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Seeds a tenant, a user and a membership with the given roles and grants.</summary>
    public async Task<SeededPrincipal> SeedAsync(
        string label,
        IEnumerable<string> permissions,
        IEnumerable<(ScopeGrantKind Kind, Guid? TargetId)> scopeGrants,
        bool membershipIsActive = true,
        bool tenantIsActive = true)
    {
        var tenantId = TenantId.New();
        var userId = UserId.New();
        var membershipId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();

        var tenant = new Tenant(tenantId, label, $"{label}-{Guid.NewGuid():N}"[..20]);
        if (!tenantIsActive)
        {
            Deactivate(tenant);
        }

        db.Tenants.Add(tenant);
        db.Users.Add(new User(userId, $"{Guid.NewGuid():N}@example.com", $"{label} user"));

        var membership = new Membership(membershipId, tenantId, userId);
        if (!membershipIsActive)
        {
            Deactivate(membership);
        }

        db.Memberships.Add(membership);

        var roleId = Guid.NewGuid();
        db.Roles.Add(new Role(roleId, tenantId, $"role-{roleId:N}"[..20]));
        db.MembershipRoles.Add(new MembershipRole(Guid.NewGuid(), tenantId, membershipId, roleId));

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(Guid.NewGuid(), tenantId, roleId, permission));
        }

        foreach (var (kind, targetId) in scopeGrants)
        {
            db.ScopeGrants.Add(new ScopeGrant(Guid.NewGuid(), tenantId, membershipId, kind, targetId));
        }

        await db.SaveChangesAsync();

        return new SeededPrincipal(tenantId, userId, membershipId);
    }

    // IsActive has a private setter by design — the domain has no deactivation command yet.
    // Reflection here rather than widening the domain for a test: 001-P6 owns lifecycle.
    private static void Deactivate(object entity) =>
        entity.GetType()
            .GetProperty("IsActive")!
            .SetValue(entity, false);
}

public sealed record SeededPrincipal(TenantId TenantId, UserId UserId, Guid MembershipId);

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
