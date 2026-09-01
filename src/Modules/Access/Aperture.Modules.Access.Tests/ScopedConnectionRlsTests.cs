using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aperture.Modules.Access.Tests;

/// <summary>
/// 009-P4: the revised <see cref="ScopedConnection"/> proven at the DBMS on a real PostgreSQL
/// container. Where <see cref="ScopeRlsTests"/> proved the policy directly with hand-written Npgsql,
/// this proves the <em>wrapper</em> — the only door — carries the same guarantee end to end:
/// reader role, read-only transaction, transaction-local session context, the P2 fragment as the
/// first belt, and the observability the plan requires.
/// <para>
/// The two tests that make the guarantee structural rather than by-convention are the anti-bypass
/// (edge 16) and the pooling-leak (edge 19): a caller who defeats the in-app belt still gets only
/// in-scope rows, and context set on one pooled connection does not survive into the next read.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopedConnectionRlsTests(PostgresFixture postgres)
{
    static ScopedConnectionRlsTests() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private readonly TenantId _tenant = TenantId.New();
    private readonly TenantId _otherTenant = TenantId.New();
    private readonly UserId _ivanov = UserId.New();
    private readonly UserId _petrova = UserId.New();
    private readonly Guid _teamA = Guid.NewGuid();

    private readonly Guid _self = Guid.NewGuid();     // tenant T, owner ivanov, all scope cols NULL
    private readonly Guid _team = Guid.NewGuid();     // tenant T, owner petrova, team A
    private readonly Guid _bare = Guid.NewGuid();     // tenant T, owner petrova, all scope cols NULL
    private readonly Guid _foreign = Guid.NewGuid();  // OTHER tenant, matches every grant on its cols

    // The caller query the wrapper wraps as a subquery. It projects the scope columns so the P2
    // belt (the wrapper's outer WHERE) can reference them under alias "t".
    private const string ProbeSql =
        "SELECT id, tenant_id, owner_user_id, team_id, region_id, account_id FROM scope_probe.rows";

    private static readonly ScopeColumns Columns = ScopeColumns.For("t");

    private IReadOnlyCollection<Guid> AllSeeded =>
        new[] { _self, _team, _bare, _foreign };

    private async Task SeedAsync()
    {
        await using var db = postgres.CreateScopeProbeContext();
        db.Rows.AddRange(
            Row(_self, _tenant, _ivanov),
            Row(_team, _tenant, _petrova, team: _teamA),
            Row(_bare, _tenant, _petrova),
            Row(_foreign, _otherTenant, _ivanov, team: _teamA));
        await db.SaveChangesAsync();
    }

    private static ScopedRow Row(Guid id, TenantId tenant, UserId owner, Guid? team = null) =>
        new() { Id = id, TenantId = tenant, OwnerUserId = owner, TeamId = team };

    private DataScopeSet Scope(params DataScope[] scopes) => DataScopeSet.Of(_tenant, scopes);

    private static NpgsqlDataSource Reader(string connectionString) =>
        NpgsqlDataSource.Create(connectionString);

    private sealed record ProbeDto(
        Guid Id,
        Guid TenantId,
        Guid OwnerUserId,
        Guid? TeamId,
        Guid? RegionId,
        Guid? AccountId);

    // --- The happy path: the wrapper filters to the principal's scope ---------------------------

    [Fact]
    public async Task Query_through_the_wrapper_returns_only_in_scope_rows()
    {
        await SeedAsync();
        await using var reader = Reader(postgres.ReaderConnectionString);
        var sut = new ScopedConnection(reader, new CapturingLogger<ScopedConnection>());

        var rows = await sut.QueryAsync<ProbeDto>(
            Scope(new DataScope.Self(_ivanov)), Columns, ProbeSql);

        var ids = rows.Select(r => r.Id).Intersect(AllSeeded).OrderBy(x => x);
        Assert.Equal(new[] { _self }, ids);
    }

    // --- Edge 16: the anti-bypass test — RLS holds even when the in-app belt is defeated ---------

    [Fact]
    public async Task Reader_returns_only_in_scope_rows_even_when_the_in_app_belt_is_defeated()
    {
        await SeedAsync();
        await using var reader = Reader(postgres.ReaderConnectionString);
        var logger = new CapturingLogger<ScopedConnection>();
        var sut = new ScopedConnection(reader, logger);

        // Hostile caller SQL: it fakes the scope columns in its projection so that the wrapper's
        // outer belt (the P2 fragment over alias "t") admits *every* row — the tenant and owner it
        // sees are the principal's own, for every row. This is the equivalent, under the no-splice
        // design, of "OR-ing away / commenting out / omitting" the fragment: the in-app belt is
        // inert. If the belt were the guarantee, this would leak the other tenant's row.
        var scopes = Scope(new DataScope.Self(_ivanov));
        var defeatingSql =
            $"""
             SELECT id,
                    '{_tenant.Value}'::uuid AS tenant_id,
                    '{_ivanov.Value}'::uuid AS owner_user_id,
                    team_id, region_id, account_id
             FROM scope_probe.rows
             """;

        var rows = await sut.QueryAsync<ProbeDto>(scopes, Columns, defeatingSql);

        // RLS re-filters below the string: only ivanov's own real row in tenant T comes back. The
        // foreign-tenant row that the defeated belt would have admitted is gone — because the
        // reader role's policy filtered the base table before the belt ever saw it.
        var ids = rows.Select(r => r.Id).Intersect(AllSeeded).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { _self }, ids);
        Assert.DoesNotContain(_foreign, ids);
        Assert.DoesNotContain(_bare, ids);
    }

    // --- Edge 19: transaction-local context does not leak across pooled reuse --------------------

    [Fact]
    public async Task Two_reads_on_one_pooled_reader_connection_do_not_leak_context()
    {
        await SeedAsync();

        // MaxPoolSize=1 forces both reads onto the same physical connection, so if the first read's
        // set_config survived its transaction the second would see it. is_local => true (SET LOCAL)
        // is what keeps them apart.
        var single = new NpgsqlConnectionStringBuilder(postgres.ReaderConnectionString)
        {
            MaxPoolSize = 1,
        }.ConnectionString;

        await using var reader = Reader(single);
        var sut = new ScopedConnection(reader, new CapturingLogger<ScopedConnection>());

        var first = await sut.QueryAsync<ProbeDto>(
            Scope(new DataScope.Self(_ivanov)), Columns, ProbeSql);
        var second = await sut.QueryAsync<ProbeDto>(
            Scope(new DataScope.Team(_teamA)), Columns, ProbeSql);

        Assert.Equal(new[] { _self }, first.Select(r => r.Id).Intersect(AllSeeded).OrderBy(x => x));

        // The second read sees only the team row — not ivanov's self row from the first read's
        // context. A leak would have added _self here.
        var secondIds = second.Select(r => r.Id).Intersect(AllSeeded).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { _team }, secondIds);
        Assert.DoesNotContain(_self, secondIds);
    }

    // --- Observability: empty-scope Information, unset-context Warning ---------------------------

    [Fact]
    public async Task An_empty_scope_set_logs_at_Information_and_returns_nothing()
    {
        await SeedAsync();
        await using var reader = Reader(postgres.ReaderConnectionString);
        var logger = new CapturingLogger<ScopedConnection>();
        var sut = new ScopedConnection(reader, logger);

        var rows = await sut.QueryAsync<ProbeDto>(DataScopeSet.None(_tenant), Columns, ProbeSql);

        Assert.Empty(rows.Select(r => r.Id).Intersect(AllSeeded));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("empty scope set", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task An_unresolved_tenant_context_logs_at_Warning_and_returns_zero_rows()
    {
        await SeedAsync();
        await using var reader = Reader(postgres.ReaderConnectionString);
        var logger = new CapturingLogger<ScopedConnection>();
        var sut = new ScopedConnection(reader, logger);

        // A principal carrying the default/empty tenant is a wiring bug. RLS turns it into a silent
        // zero-row read; the wrapper makes it loud (the one failure RLS introduces).
        var unresolved = DataScopeSet.Of(new TenantId(Guid.Empty), new DataScope.AllTenant());

        var rows = await sut.QueryAsync<ProbeDto>(unresolved, Columns, ProbeSql);

        Assert.Empty(rows.Select(r => r.Id).Intersect(AllSeeded));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("without an established tenant context", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
