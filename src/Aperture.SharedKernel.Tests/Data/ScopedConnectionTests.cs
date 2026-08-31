using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.Extensions.Logging;

namespace Aperture.SharedKernel.Tests.Data;

/// <summary>
/// 009-P3 — <see cref="ScopedConnection"/> is the only door to raw SQL, and it cannot be opened
/// without a <see cref="DataScopeSet"/> and a <see cref="ScopeColumns"/>. These assert the shape of
/// that door (no unscoped overload), its fail-closed behaviour (no placeholder → throw), and its
/// observability (empty-scope log line; span carries tenant id and scope-kind counts but never
/// scope values).
/// </summary>
public sealed class ScopedConnectionTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    // --- The public surface admits no unscoped query overload (edge: compile-level guarantee) -----

    [Fact]
    public void Every_query_method_requires_a_scope_set_and_scope_columns()
    {
        var queryMethods = typeof(ScopedConnection)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Query", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(queryMethods);

        foreach (var method in queryMethods)
        {
            var parameterTypes = method.GetParameters().Select(p => p.ParameterType).ToList();
            Assert.True(
                parameterTypes.Contains(typeof(DataScopeSet)),
                $"{method.Name} has no DataScopeSet parameter — that is an unscoped raw-read door.");
            Assert.True(
                parameterTypes.Contains(typeof(ScopeColumns)),
                $"{method.Name} has no ScopeColumns parameter — the caller could not name the scoped columns.");
        }
    }

    // --- Fail closed ------------------------------------------------------------------------------

    [Fact]
    public async Task A_query_without_the_scope_placeholder_is_rejected()
    {
        var sut = new ScopedConnection(new UnusableConnection(), NullLogger());
        var scopes = DataScopeSet.Of(Tenant, new DataScope.Self(new UserId(Guid.NewGuid())));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.QueryAsync<int>(scopes, ScopeColumns.For("o"), "SELECT id FROM orders o WHERE o.status = 1"));
        Assert.Contains(ScopedConnection.ScopePlaceholder, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_arguments_are_rejected_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() => new ScopedConnection(null!, NullLogger()));
        Assert.Throws<ArgumentNullException>(() => new ScopedConnection(new UnusableConnection(), null!));
    }

    // --- Observability: the empty-scope log line --------------------------------------------------

    [Fact]
    public async Task An_empty_scope_set_emits_an_Information_log_line()
    {
        var logger = new RecordingLogger();
        var sut = new ScopedConnection(new UnusableConnection(), logger);
        var empty = DataScopeSet.None(Tenant);

        // The connection cannot execute; we only care that the log fires during composition, which
        // happens before the database is touched.
        await RunAndIgnoreDbFailure(() =>
            sut.QueryAsync<int>(empty, ScopeColumns.For("o"), $"SELECT id FROM orders o WHERE {ScopedConnection.ScopePlaceholder}"));

        var line = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("empty scope set", line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Tenant.Value.ToString(), line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_empty_scope_set_emits_no_empty_scope_log_line()
    {
        var logger = new RecordingLogger();
        var sut = new ScopedConnection(new UnusableConnection(), logger);
        var scopes = DataScopeSet.Of(Tenant, new DataScope.AllTenant());

        await RunAndIgnoreDbFailure(() =>
            sut.QueryAsync<int>(scopes, ScopeColumns.For("o"), $"SELECT id FROM orders o WHERE {ScopedConnection.ScopePlaceholder}"));

        Assert.Empty(logger.Entries);
    }

    // --- Observability: the span carries counts, never values -------------------------------------

    [Fact]
    public async Task The_span_carries_tenant_id_and_scope_kind_counts()
    {
        var captured = new List<Activity>();
        using var listener = ListenFor(captured);

        var sut = new ScopedConnection(new UnusableConnection(), NullLogger());
        var scopes = DataScopeSet.Of(
            Tenant,
            new DataScope.Team(Guid.NewGuid()),
            new DataScope.Team(Guid.NewGuid()),
            new DataScope.Region(Guid.NewGuid()));

        await RunAndIgnoreDbFailure(() =>
            sut.QueryAsync<int>(scopes, ScopeColumns.For("o"), $"SELECT id FROM orders o WHERE {ScopedConnection.ScopePlaceholder}"));

        var span = Assert.Single(captured);
        Assert.Equal(Tenant.Value, span.GetTagItem("aperture.tenant_id"));
        Assert.Equal(3, span.GetTagItem("aperture.scope.count"));
        Assert.Equal(2, span.GetTagItem("aperture.scope.team"));
        Assert.Equal(1, span.GetTagItem("aperture.scope.region"));
        Assert.Equal(0, span.GetTagItem("aperture.scope.self"));
        Assert.Equal(0, span.GetTagItem("aperture.scope.account"));
        Assert.Equal(0, span.GetTagItem("aperture.scope.all_tenant"));
    }

    [Fact]
    public async Task The_span_never_carries_a_scope_grant_value()
    {
        var captured = new List<Activity>();
        using var listener = ListenFor(captured);

        var teamId = Guid.NewGuid();
        var regionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var sut = new ScopedConnection(new UnusableConnection(), NullLogger());
        var scopes = DataScopeSet.Of(
            Tenant,
            new DataScope.Self(new UserId(userId)),
            new DataScope.Team(teamId),
            new DataScope.Region(regionId),
            new DataScope.Account(accountId));

        await RunAndIgnoreDbFailure(() =>
            sut.QueryAsync<int>(scopes, ScopeColumns.For("o"), $"SELECT id FROM orders o WHERE {ScopedConnection.ScopePlaceholder}"));

        var span = Assert.Single(captured);
        var tagText = string.Join("\n", span.Tags.Select(t => $"{t.Key}={t.Value}"));

        // Grant values are row identifiers and must not appear anywhere in telemetry.
        foreach (var secret in new[] { teamId, regionId, accountId, userId })
        {
            Assert.DoesNotContain(secret.ToString(), tagText, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Helpers ----------------------------------------------------------------------------------

    private static async Task RunAndIgnoreDbFailure(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            // The stub connection cannot execute SQL; composition (span + log) already ran.
        }
    }

    private static ActivityListener ListenFor(List<Activity> sink)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScopedConnection.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = sink.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static ILogger<ScopedConnection> NullLogger() => new RecordingLogger();

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger<ScopedConnection>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    // A DbConnection that parses but cannot execute — enough for Dapper to attempt to open it and
    // fail, after composition has already emitted the span and log we assert on.
    private sealed class UnusableConnection : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => string.Empty; set { } }

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open() => throw new NotSupportedException("No database in this unit test.");

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
