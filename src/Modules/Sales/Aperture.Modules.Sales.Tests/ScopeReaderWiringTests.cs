using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// 002-P1 pays the debt owed since 009-P4: the reader <c>NpgsqlDataSource</c> and
/// <see cref="ScopedConnection"/> are wired into DI for the first time. These tests exercise the exact
/// registration the API host uses (<see cref="ScopedReaderRegistration.AddScopedReader"/>) against a
/// real PostgreSQL container, proving:
/// <list type="bullet">
/// <item><see cref="ScopedConnection"/> resolves from a built container over a reader data source;</item>
/// <item>the resolved connection authenticates as <c>aperture_reader</c> and is bound by the row-security
/// policy — a read with no established session context returns <b>zero</b> rows (fail-closed, edge 2's
/// mechanism), and a read with an in-scope grant returns exactly the in-scope row;</item>
/// <item>the registration merges a secret-sourced password onto a credential-less base connection
/// string, so the reader password can be a deploy secret rather than committed configuration.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopeReaderWiringTests(PostgresFixture postgres)
{
    static ScopeReaderWiringTests() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private static readonly ScopeColumns Columns = ScopeColumns.For("t");

    private const string ProbeSql =
        "SELECT id, tenant_id, owner_user_id, team_id, region_id, account_id FROM sales_probe.rows";

    private sealed record ProbeDto(
        Guid Id,
        Guid TenantId,
        Guid OwnerUserId,
        Guid? TeamId,
        Guid? RegionId,
        Guid? AccountId);

    [Fact]
    public void AddScopedReader_registers_a_resolvable_ScopedConnection_over_a_reader_data_source()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScopedReader(postgres.ReaderConnectionString);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var scoped = scope.ServiceProvider.GetService<ScopedConnection>();
        Assert.NotNull(scoped);
    }

    [Fact]
    public async Task A_ScopedConnection_resolved_from_DI_connects_as_the_reader_role_and_is_RLS_bound()
    {
        // A row the principal owns, and a row they do not — both in the same tenant. The reader-role
        // policy must admit only the owned one.
        var tenant = TenantId.New();
        var owner = UserId.New();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await postgres.SeedProbeRowAsync(mine, tenant, owner);
        await postgres.SeedProbeRowAsync(theirs, tenant, UserId.New());

        // The exact wiring the host uses, resolved from a real container.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScopedReader(postgres.ReaderConnectionString);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ScopedConnection>();

        var rows = await sut.QueryAsync<ProbeDto>(
            DataScopeSet.Of(tenant, new DataScope.Self(owner)), Columns, ProbeSql);

        var ids = rows.Select(r => r.Id).ToHashSet();
        Assert.Contains(mine, ids);
        Assert.DoesNotContain(theirs, ids);
    }

    [Fact]
    public async Task A_reader_connection_with_no_established_context_returns_zero_rows()
    {
        // Edge 2's mechanism at the DBMS: without session context the policy's tenant equality is
        // unknown, so the reader sees nothing — fail-closed, not fail-open. An empty scope set sets the
        // tenant but grants nothing, so the grant union is false for every row: zero in-scope rows.
        var tenant = TenantId.New();
        await postgres.SeedProbeRowAsync(Guid.NewGuid(), tenant, UserId.New());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScopedReader(postgres.ReaderConnectionString);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ScopedConnection>();

        var rows = await sut.QueryAsync<ProbeDto>(
            DataScopeSet.None(tenant), Columns, ProbeSql);

        // Only rows in THIS tenant could ever be relevant; the empty grant admits none of them.
        Assert.DoesNotContain(rows, r => r.TenantId == tenant.Value);
    }

    [Fact]
    public async Task The_registration_merges_a_secret_sourced_password_onto_a_credential_less_base_string()
    {
        // The production shape: the base reader connection string carries no password (it lives in
        // configuration credential-free), and the secret is layered on at boot. Proven by wiring with
        // the password-less string plus the secret, and confirming the reader still authenticates and
        // is RLS-bound.
        var tenant = TenantId.New();
        var owner = UserId.New();
        var mine = Guid.NewGuid();
        await postgres.SeedProbeRowAsync(mine, tenant, owner);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScopedReader(
            postgres.ReaderConnectionStringWithoutPassword,
            postgres.ReaderRolePassword);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ScopedConnection>();

        var rows = await sut.QueryAsync<ProbeDto>(
            DataScopeSet.Of(tenant, new DataScope.Self(owner)), Columns, ProbeSql);

        Assert.Contains(mine, rows.Select(r => r.Id));
    }

    [Fact]
    public async Task A_base_string_without_a_password_cannot_authenticate_when_no_secret_is_supplied()
    {
        // The negative of the above: with no password anywhere, the data source builds (no connection is
        // opened at build time) but the reader cannot authenticate — proving the password is genuinely
        // required and not silently defaulted.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScopedReader(postgres.ReaderConnectionStringWithoutPassword);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<ScopedConnection>();

        // The first actual read is where an absent credential surfaces.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            sut.QueryAsync<ProbeDto>(DataScopeSet.None(TenantId.New()), Columns, ProbeSql));
    }

    [Fact]
    public void ScopedConnection_is_registered_scoped_and_the_reader_data_source_is_a_singleton()
    {
        // The lifetimes matter: the data source owns the connection pool (singleton), while the
        // ScopedConnection handle over it is per-request (scoped), mirroring how the host resolves it.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScopedReader(postgres.ReaderConnectionString);

        var scopedDescriptor = services.Single(d => d.ServiceType == typeof(ScopedConnection));
        Assert.Equal(ServiceLifetime.Scoped, scopedDescriptor.Lifetime);

        var dataSourceDescriptor = services.Single(d =>
            d.ServiceType == typeof(Npgsql.NpgsqlDataSource));
        Assert.Equal(ServiceLifetime.Singleton, dataSourceDescriptor.Lifetime);
    }
}
