using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Aperture.SharedKernel.Authorization;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Aperture.SharedKernel.Data;

/// <summary>
/// The one and only door to raw SQL (009-P3). Dapper is referenced by exactly this project, and an
/// architecture test (<c>RawSqlIsScopedTests</c>) proves it, so no other code can reach
/// <c>connection.QueryAsync</c> at all. A raw read therefore <em>cannot</em> be issued without a
/// <see cref="DataScopeSet"/> and a <see cref="ScopeColumns"/>: there is no query overload that
/// omits either, and the tenant-scoped predicate is composed here rather than handed to the caller
/// to interpolate — the correct path is the only path, because raw SQL is the one place where
/// fail-open is the path of least effort (CLAUDE.md invariant 2, 3).
/// <para>
/// The caller writes the <c>SELECT … FROM … WHERE …</c> and marks where the scope predicate goes
/// with <see cref="ScopePlaceholder"/>. This wrapper substitutes the tenant-scoped fragment built
/// from <see cref="ScopeSql.ToSqlFragment"/> and merges its bound parameters. A query string that
/// omits the placeholder is rejected (<see cref="ArgumentException"/>) — an unscoped raw read is a
/// cross-tenant leak, so it fails closed at the call site rather than running unfiltered.
/// </para>
/// <para>
/// <b>Reads only.</b> No <c>Execute</c>/<c>ExecuteScalar</c> write path exists here by design
/// (009 out-of-scope); adding one is a design change. Reads run outside the EF change tracker, so a
/// query here sees committed state, not a same-request uncommitted EF write — callers needing
/// read-your-writes must read through EF. No transaction is opened; the wrapper participates in an
/// ambient one if the connection already has it.
/// </para>
/// </summary>
public sealed class ScopedConnection
{
    /// <summary>
    /// The token a caller places in its <c>WHERE</c> where the tenant-scoped predicate belongs,
    /// e.g. <c>WHERE o.status = @status AND /**scope**/</c>. Substituted with the parenthesised
    /// scope fragment. Shaped as a SQL comment so a query that forgets to run through this wrapper
    /// still parses (and then leaks) — which is exactly why the missing token fails closed here.
    /// </summary>
    public const string ScopePlaceholder = "/**scope**/";

    /// <summary>
    /// The <see cref="ActivitySource"/> name a host subscribes to for scoped-read spans. Tags carry
    /// the tenant id and per-kind scope <em>counts</em> only — never scope values, which are row
    /// identifiers and do not belong in telemetry.
    /// </summary>
    public const string ActivitySourceName = "Aperture.SharedKernel.Data.ScopedConnection";

    private static readonly ActivitySource Activity = new(ActivitySourceName);

    private readonly DbConnection _connection;
    private readonly ILogger<ScopedConnection> _logger;

    public ScopedConnection(DbConnection connection, ILogger<ScopedConnection> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs <paramref name="sql"/> as a scoped read, returning every matching row. The tenant-scoped
    /// predicate is composed from <paramref name="scopes"/> and <paramref name="columns"/> and spliced
    /// in at <see cref="ScopePlaceholder"/>; <paramref name="parameters"/> supplies the caller's own
    /// bound values (never scope values — those are added here).
    /// </summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        DataScopeSet scopes,
        ScopeColumns columns,
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var command = Compose<T>(scopes, columns, sql, parameters, cancellationToken);
        var rows = await _connection.QueryAsync<T>(command).ConfigureAwait(false);
        return rows.AsList();
    }

    /// <summary>
    /// Runs <paramref name="sql"/> as a scoped read expected to match at most one row. Same scoping
    /// contract as <see cref="QueryAsync{T}"/> — the <see cref="DataScopeSet"/> and
    /// <see cref="ScopeColumns"/> are required and the tenant predicate is composed here.
    /// </summary>
    public async Task<T?> QuerySingleOrDefaultAsync<T>(
        DataScopeSet scopes,
        ScopeColumns columns,
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var command = Compose<T>(scopes, columns, sql, parameters, cancellationToken);
        return await _connection.QuerySingleOrDefaultAsync<T>(command).ConfigureAwait(false);
    }

    // Build the final command: splice the tenant-scoped fragment into the caller's SQL, merge the
    // caller's parameters with the fragment's bound scope parameters, and open the telemetry span.
    // Every raw read funnels through here, so this is the single place the scope predicate is
    // guaranteed to be present.
    private CommandDefinition Compose<T>(
        DataScopeSet scopes,
        ScopeColumns columns,
        string sql,
        object? parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        if (!sql.Contains(ScopePlaceholder, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The query does not contain the scope placeholder '{ScopePlaceholder}'. Every raw "
                + "read must mark where the tenant-scoped predicate belongs, or it runs unfiltered "
                + "across tenants. Place it inside the WHERE clause, e.g. \"... AND "
                + $"{ScopePlaceholder}\".",
                nameof(sql));
        }

        var fragment = scopes.ToSqlFragment(columns);
        var scopedSql = sql.Replace(ScopePlaceholder, $"({fragment.Sql})", StringComparison.Ordinal);

        var dynamic = new DynamicParameters();
        if (parameters is not null)
        {
            dynamic.AddDynamicParams(parameters);
        }

        foreach (var (name, value) in fragment.Parameters)
        {
            dynamic.Add(name, value);
        }

        Observe(scopes, columns);

        return new CommandDefinition(scopedSql, dynamic, commandType: CommandType.Text, cancellationToken: cancellationToken);
    }

    // Opens a span tagged with the tenant id and per-kind scope counts (never scope values), and —
    // because "a user sees nothing" is a support ticket that must be one log line, not a debugging
    // session — logs at Information when the scope set is empty and the read is scoped to return
    // nothing by design.
    private void Observe(DataScopeSet scopes, ScopeColumns columns)
    {
        var tenantId = scopes.TenantId.Value;

        using var activity = Activity.StartActivity("ScopedConnection.Query", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("aperture.tenant_id", tenantId);
            activity.SetTag("aperture.scope.alias", columns.Alias);
            activity.SetTag("aperture.scope.count", scopes.Count);
            activity.SetTag("aperture.scope.self", scopes.Scopes.Count(s => s is DataScope.Self));
            activity.SetTag("aperture.scope.team", scopes.Scopes.Count(s => s is DataScope.Team));
            activity.SetTag("aperture.scope.region", scopes.Scopes.Count(s => s is DataScope.Region));
            activity.SetTag("aperture.scope.account", scopes.Scopes.Count(s => s is DataScope.Account));
            activity.SetTag("aperture.scope.all_tenant", scopes.Scopes.Count(s => s is DataScope.AllTenant));
        }

        if (scopes.IsEmpty)
        {
            _logger.LogInformation(
                "Scoped read for tenant {TenantId} has an empty scope set and returns no rows by "
                + "design; the principal has no data scopes granted.",
                tenantId);
        }
    }
}
