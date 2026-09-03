using Aperture.Modules.Sales.Persistence;
using Aperture.SharedKernel.Data.RowLevelSecurity;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// A real PostgreSQL, migrated by the real Sales migration. Mirrors the Access fixture: not SQLite and
/// not the in-memory provider, because the things P1 asserts — the migration creating the schema, and
/// the reader role's row-security policy returning zero rows without session context — either do not
/// exist or behave differently on any substitute.
/// <para>
/// One difference from Access: the Sales migration does not create the <c>aperture_reader</c> role
/// (that role is provisioned once, by an Access migration, and is a shared-kernel concept). So this
/// fixture provisions it here — <c>CREATE ROLE</c> + a test password — exactly as production does out
/// of band from a deploy secret. It then adopts the RLS convention on a probe table so a reader-role
/// connection is subject to the same policy a real Sales table will carry in P2+.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("aperture")
        .WithUsername("aperture")
        .WithPassword("aperture")
        .Build();

    /// <summary>The reader role's test password. In production this comes from a deploy secret and the
    /// role is created password-less; here the fixture provisions both, standing in for that step.</summary>
    private const string ReaderPassword = "aperture_reader";

    public const string ProbeSchema = "sales_probe";

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// The same database as <see cref="ConnectionString"/>, but authenticated as the least-privilege
    /// <c>aperture_reader</c> role — the role row-security policies bind to. A connection on this string
    /// is subject to RLS; a connection on <see cref="ConnectionString"/> (the owner role) bypasses it.
    /// </summary>
    public string ReaderConnectionString =>
        new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Username = ScopeRlsPolicy.ReaderRole,
            Password = ReaderPassword,
        }.ConnectionString;

    /// <summary>The reader connection string with no password — the shape production stores in
    /// configuration, before the secret is layered on at boot. Used to prove the registration merges
    /// the secret rather than depending on it already being in the base string.</summary>
    public string ReaderConnectionStringWithoutPassword =>
        new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Username = ScopeRlsPolicy.ReaderRole,
            Password = null,
        }.ConnectionString;

    public string ReaderRolePassword => ReaderPassword;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Migrate once per fixture with the real migration rather than EnsureCreated. This creates the
        // `sales` schema and the sales.__migrations history table.
        await using var context = CreateContext(TenantId.New());
        await context.Database.MigrateAsync();

        // Provision the reader role the Sales migration deliberately does not create, then give it a
        // password so tests can authenticate as it. Idempotent, so re-running the fixture is safe.
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await ExecuteAsync(
            connection,
            $"""
             DO $$
             BEGIN
                 IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{ScopeRlsPolicy.ReaderRole}') THEN
                     CREATE ROLE {ScopeRlsPolicy.ReaderRole} LOGIN;
                 END IF;
             END
             $$;
             ALTER ROLE {ScopeRlsPolicy.ReaderRole}
                 NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT;
             ALTER ROLE {ScopeRlsPolicy.ReaderRole} PASSWORD '{ReaderPassword}';
             """);

        // A probe table that carries the five scope columns, then the RLS convention on it. Applied as
        // the owner role (bypasses RLS), so only reader-role connections see the policy. This stands in
        // for a real Sales table until P2 lands accounts; the wiring proof does not need a domain table.
        await ExecuteAsync(
            connection,
            $"""
             CREATE SCHEMA IF NOT EXISTS {ProbeSchema};
             CREATE TABLE IF NOT EXISTS {ProbeSchema}.rows (
                 id uuid PRIMARY KEY,
                 tenant_id uuid NOT NULL,
                 owner_user_id uuid NOT NULL,
                 team_id uuid NULL,
                 region_id uuid NULL,
                 account_id uuid NULL
             );
             """);

        await ExecuteAsync(connection, ScopeRlsPolicy.Enable(ProbeSchema, "rows"));
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public SalesDbContext CreateContext(TenantId tenant) =>
        new(
            new DbContextOptionsBuilder<SalesDbContext>().UseSalesNpgsql(ConnectionString).Options,
            new FixedTenantContext(tenant));

    /// <summary>Seeds a probe row through the owner connection (which bypasses RLS), so the reader-role
    /// read under test is the only thing the policy is filtering.</summary>
    public async Task SeedProbeRowAsync(
        Guid id,
        TenantId tenant,
        UserId owner,
        Guid? team = null,
        Guid? region = null,
        Guid? account = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
             INSERT INTO {ProbeSchema}.rows (id, tenant_id, owner_user_id, team_id, region_id, account_id)
             VALUES (@id, @tenant, @owner, @team, @region, @account)
             """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("owner", owner.Value);
        command.Parameters.AddWithValue("team", (object?)team ?? DBNull.Value);
        command.Parameters.AddWithValue("region", (object?)region ?? DBNull.Value);
        command.Parameters.AddWithValue("account", (object?)account ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedTenantContext(TenantId tenantId) : ITenantContext
    {
        public bool HasTenant => true;

        public TenantId TenantId => tenantId;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
