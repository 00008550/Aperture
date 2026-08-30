using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Aperture.Modules.Access.Tests;

/// <summary>
/// A real PostgreSQL, migrated by the real migration.
/// <para>
/// Not SQLite and not the in-memory provider. The things this portion is actually asserting —
/// a check constraint, a partial-free unique index, the migration itself — either do not exist
/// or behave differently on any substitute, so a test against one would prove the wrong thing.
/// </para>
/// <para>
/// Pinned to the same image the compose file uses, so a developer with the stack already up has
/// the layer cached.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("aperture")
        .WithUsername("aperture")
        .WithPassword("aperture")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Migrate once per fixture, with the real migration rather than EnsureCreated. A test
        // suite that uses EnsureCreated never runs the migrations it is supposed to be
        // protecting, and the first broken migration reaches production green.
        await using var context = CreateContext(TenantId.New());
        await context.Database.MigrateAsync();

        // The 001-P4 probe table. Created here so every test in the collection can assume it.
        await using var probe = CreateScopeProbeContext();
        await probe.Database.ExecuteSqlRawAsync(ScopeProbeDbContext.CreateTableSql);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public AccessDbContext CreateContext(TenantId tenant) =>
        new(
            new DbContextOptionsBuilder<AccessDbContext>().UseAccessNpgsql(ConnectionString).Options,
            new FixedTenantContext(tenant));

    /// <summary>The 001-P4 scope-translation probe. Not tenant-filtered: the point of the test
    /// is that the <em>scope predicate</em> carries the tenant boundary itself, so a global query
    /// filter here would hide a fail-open translation.</summary>
    public ScopeProbeDbContext CreateScopeProbeContext() =>
        new(new DbContextOptionsBuilder<ScopeProbeDbContext>().UseNpgsql(ConnectionString).Options);

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
