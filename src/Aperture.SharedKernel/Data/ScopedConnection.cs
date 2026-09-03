using System.Diagnostics;
using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Data.RowLevelSecurity;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aperture.SharedKernel.Data;

/// <summary>
/// The one and only door to raw SQL (009-P4). Dapper and Npgsql are referenced by exactly this
/// project, and an architecture test (<c>RawSqlIsScopedTests</c>) proves it, so no other code can
/// reach <c>connection.QueryAsync</c> at all. A raw read therefore <em>cannot</em> be issued without
/// a <see cref="DataScopeSet"/> and a <see cref="ScopeColumns"/>: there is no query overload that
/// omits either.
/// <para>
/// <b>The scope guarantee is structural, not by-convention.</b> Every read runs as the dedicated,
/// least-privilege <see cref="ScopeRlsPolicy.ReaderRole"/> inside a read-only transaction whose
/// session context is established from the <see cref="DataScopeSet"/> via
/// <see cref="ScopeSessionContext"/>. The row-security policy the reader role is bound to re-asserts
/// tenant + scope on every row <em>below</em> the SQL string — so no <c>OR</c>, comment, unbalanced
/// paren, or omitted filter in the caller's text can widen the result past the principal's scope. A
/// connection whose context was never set returns <b>zero</b> rows, not everything (fail-closed).
/// This is the fail-open the <c>/**scope**/</c> placeholder design could not close, and why that
/// mechanism was removed: no in-app string composition is structural.
/// </para>
/// <para>
/// The in-app P2 fragment (<see cref="ScopeSql.ToSqlFragment"/>) is still <c>AND</c>-ed in, as a
/// belt-and-suspenders first filter and a query-plan aid — the caller's query is wrapped as a
/// subquery under the <see cref="ScopeColumns.Alias"/> it names, and the fragment filters the
/// wrapper's outer <c>WHERE</c>. There is <b>no placeholder</b> and the caller never splices the
/// scope term into its own <c>WHERE</c>. Even if that belt is defeated, RLS holds (edge case 16).
/// </para>
/// <para>
/// <b>Reads only.</b> No <c>Execute</c>/<c>ExecuteScalar</c> write path exists here by design (009
/// out-of-scope); adding one is a design change. Each read opens its own read-only transaction —
/// session context must be transaction-local (<c>set_config(..., is_local =&gt; true)</c>) or it
/// leaks across pooled connection reuse (edge case 19). Reads run outside the EF change tracker on a
/// separate reader connection, so a query here sees committed state, not a same-request uncommitted
/// EF write — callers needing read-your-writes must read through EF.
/// </para>
/// </summary>
public sealed class ScopedConnection
{
    /// <summary>
    /// The <see cref="ActivitySource"/> name a host subscribes to for scoped-read spans. Tags carry
    /// the tenant id and per-kind scope <em>counts</em> only — never scope values, which are row
    /// identifiers and do not belong in telemetry.
    /// </summary>
    public const string ActivitySourceName = "Aperture.SharedKernel.Data.ScopedConnection";

    // The subquery alias the caller's query is wrapped under is the alias the caller named on its
    // ScopeColumns, so the fragment's alias-qualified column references resolve against it.
    private static readonly ActivitySource Activity = new(ActivitySourceName);

    // Dapper is referenced only by this project (the raw-SQL gate enforces it), so this wrapper is the
    // right and only home for Dapper's global column-matching configuration. Snake_case columns
    // (owner_user_id, created_at, …) map to a DTO's PascalCase members without every caller repeating the
    // setting or, worse, referencing Dapper to set it. A static constructor runs once, before the first
    // QueryAsync builds a deserializer.
    static ScopedConnection() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private readonly NpgsqlDataSource _reader;
    private readonly ILogger<ScopedConnection> _logger;

    /// <summary>
    /// Constructs the wrapper over the reader <see cref="NpgsqlDataSource"/> — built from the
    /// dedicated reader connection string (a configuration value distinct from the EF owner
    /// connection), whose credential comes from a deploy secret, never the code. This data source
    /// authenticates as <see cref="ScopeRlsPolicy.ReaderRole"/>, the role RLS policies bind to.
    /// </summary>
    public ScopedConnection(NpgsqlDataSource reader, ILogger<ScopedConnection> logger)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs <paramref name="sql"/> as a scoped read, returning every matching row. The read executes
    /// as the reader role with session context from <paramref name="scopes"/> established, and the
    /// P2 fragment built from <paramref name="scopes"/> and <paramref name="columns"/> applied as the
    /// first belt; <paramref name="parameters"/> supplies the caller's own bound values (never scope
    /// values — those are added here). <paramref name="sql"/> must project the
    /// <see cref="IScopedResource"/> columns named by <paramref name="columns"/> so the belt can
    /// reference them.
    /// </summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        DataScopeSet scopes,
        ScopeColumns columns,
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var (scopedSql, dynamic) = Compose(scopes, columns, sql, parameters);

        using var activity = Observe(scopes, columns);
        await using var connection = await _reader.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginScopedReadAsync(connection, scopes, cancellationToken).ConfigureAwait(false);

        var command = new CommandDefinition(scopedSql, dynamic, transaction, cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<T>(command).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.AsList();
    }

    /// <summary>
    /// Runs <paramref name="sql"/> as a scoped read expected to match at most one row. Same scoping
    /// contract as <see cref="QueryAsync{T}"/> — the <see cref="DataScopeSet"/> and
    /// <see cref="ScopeColumns"/> are required, the read runs as the reader role with session context
    /// set, and the P2 fragment is applied as the first belt.
    /// </summary>
    public async Task<T?> QuerySingleOrDefaultAsync<T>(
        DataScopeSet scopes,
        ScopeColumns columns,
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var (scopedSql, dynamic) = Compose(scopes, columns, sql, parameters);

        using var activity = Observe(scopes, columns);
        await using var connection = await _reader.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginScopedReadAsync(connection, scopes, cancellationToken).ConfigureAwait(false);

        var command = new CommandDefinition(scopedSql, dynamic, transaction, cancellationToken: cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<T>(command).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    // Wraps the caller's query as a subquery under the caller-named alias and AND-s the P2 fragment
    // into the wrapper's outer WHERE — the first belt. No placeholder, no caller-supplied splice:
    // the wrapper owns the composition, and even a caller query that defeats this belt is re-filtered
    // by RLS below the string (edge 16). Caller parameters and the fragment's bound scope parameters
    // are merged into one bag.
    private static (string Sql, DynamicParameters Parameters) Compose(
        DataScopeSet scopes,
        ScopeColumns columns,
        string sql,
        object? parameters)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        // A trailing semicolon would close the statement before the subquery wrapper's parenthesis,
        // turning the wrap into a syntax error rather than an applied filter. Reject it at the call
        // site rather than silently stripping — an edited-away belt is a bug worth surfacing.
        if (sql.TrimEnd().EndsWith(';'))
        {
            throw new ArgumentException(
                "The query must not end with a semicolon: it is wrapped as a subquery so the scope "
                + "fragment can be applied, and a trailing ';' closes the statement early.",
                nameof(sql));
        }

        var fragment = scopes.ToSqlFragment(columns);

        // SELECT {alias}.* FROM ( <caller query> ) AS {alias} WHERE ( <fragment> ).
        // The caller names {alias} on its ScopeColumns and projects the scope columns under it; the
        // fragment references {alias}.tenant_id etc., so the subquery alias and the fragment alias
        // are one and the same by construction.
        var scopedSql =
            $"""
             SELECT {columns.Alias}.* FROM (
             {sql}
             ) AS {columns.Alias} WHERE ({fragment.Sql})
             """;

        var dynamic = new DynamicParameters();
        if (parameters is not null)
        {
            dynamic.AddDynamicParams(parameters);
        }

        foreach (var (name, value) in fragment.Parameters)
        {
            dynamic.Add(name, value);
        }

        return (scopedSql, dynamic);
    }

    // Opens the read-only transaction that carries the session context, then establishes context
    // from the scope set. is_local => true (SET LOCAL semantics) scopes every setting to this
    // transaction so it cannot survive into the next read on a pooled connection (edge 19). The
    // transaction is marked READ ONLY: this is a query path and takes no row locks.
    private async Task<NpgsqlTransaction> BeginScopedReadAsync(
        NpgsqlConnection connection,
        DataScopeSet scopes,
        CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SET TRANSACTION READ ONLY", transaction: transaction, cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            var session = ScopeSessionContext.Build(scopes);
            var sessionParameters = new DynamicParameters();
            foreach (var (name, value) in session.Parameters)
            {
                sessionParameters.Add(name, value);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                session.Sql, sessionParameters, transaction, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return transaction;
    }

    // Opens a span tagged with the tenant id and per-kind scope counts (never scope values), and
    // makes RLS's one new failure mode loud: an empty scope set logs at Information ("the principal
    // sees nothing by design" — a support ticket answerable in one line), and an unresolved tenant
    // (the default/empty tenant a mis-wired principal carries) logs at Warning, because RLS turns
    // that into a silent zero-row read rather than an error.
    private Activity? Observe(DataScopeSet scopes, ScopeColumns columns)
    {
        var tenantId = scopes.TenantId.Value;

        var activity = Activity.StartActivity("ScopedConnection.Query", ActivityKind.Client);
        if (activity is not null)
        {
            var contextEstablished = tenantId != Guid.Empty;
            activity.SetTag("aperture.tenant_id", tenantId);
            activity.SetTag("aperture.scope.alias", columns.Alias);
            activity.SetTag("aperture.scope.count", scopes.Count);
            activity.SetTag("aperture.scope.self", scopes.Scopes.Count(s => s is DataScope.Self));
            activity.SetTag("aperture.scope.team", scopes.Scopes.Count(s => s is DataScope.Team));
            activity.SetTag("aperture.scope.region", scopes.Scopes.Count(s => s is DataScope.Region));
            activity.SetTag("aperture.scope.account", scopes.Scopes.Count(s => s is DataScope.Account));
            activity.SetTag("aperture.scope.all_tenant", scopes.Scopes.Count(s => s is DataScope.AllTenant));
            activity.SetTag("aperture.reader_role", ScopeRlsPolicy.ReaderRole);
            activity.SetTag("aperture.scope.context_established", contextEstablished);
        }

        if (tenantId == Guid.Empty)
        {
            // Fail-closed but loud: with no real tenant, RLS's tenant equality is unknown and the
            // read returns nothing. That is safe, but a silent empty result is a debugging session,
            // so the misconfiguration is surfaced here rather than discovered downstream.
            _logger.LogWarning(
                "Scoped read issued without an established tenant context (tenant is the empty GUID); "
                + "the reader role's row-security policy will return zero rows. This is a wiring bug, "
                + "not an empty grant.");
        }
        else if (scopes.IsEmpty)
        {
            _logger.LogInformation(
                "Scoped read for tenant {TenantId} has an empty scope set and returns no rows by "
                + "design; the principal has no data scopes granted.",
                tenantId);
        }

        return activity;
    }
}
