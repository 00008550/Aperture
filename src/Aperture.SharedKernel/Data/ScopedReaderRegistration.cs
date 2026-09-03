using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aperture.SharedKernel.Data;

/// <summary>
/// The composition-root wiring of the raw-SQL read path (the debt owed since 009-P4, paid by 002-P1).
/// <para>
/// It lives in the sanctioned wrapper project on purpose: the reader <see cref="NpgsqlDataSource"/> is
/// the one place a connection as the least-privilege <see cref="RowLevelSecurity.ScopeRlsPolicy.ReaderRole"/>
/// is constructed, and keeping its construction here — beside <see cref="ScopedConnection"/>, the only
/// door to raw SQL — means the host never touches an <c>NpgsqlDataSource</c> or a raw connection
/// directly. The same registration is exercised by the Sales reader-wiring test, so what the host runs
/// is what the test proves.
/// </para>
/// <para>
/// <b>The reader is not the EF owner.</b> The connection string here is a value distinct from the EF
/// owner connection, its username is the reader role, and its password comes from a deploy secret
/// supplied by the caller (<paramref name="readerPassword"/>) — never committed. The
/// <c>AddScopeReaderRole</c> migration (009-P3) creates the role password-less precisely so the
/// credential is provisioned out of band. A data source built here authenticates as the reader role,
/// which the row-security policies bind to; a query issued through it that establishes no session
/// context returns zero rows (fail-closed), proven by the wiring test.
/// </para>
/// </summary>
public static class ScopedReaderRegistration
{
    /// <summary>
    /// Registers the reader <see cref="NpgsqlDataSource"/> (singleton — it owns the connection pool)
    /// and <see cref="ScopedConnection"/> (scoped — a per-request handle over that pool). The reader
    /// connection string is merged with <paramref name="readerPassword"/> when one is supplied, so the
    /// base string can live in configuration credential-free while the secret is layered on at boot.
    /// </summary>
    public static IServiceCollection AddScopedReader(
        this IServiceCollection services,
        string readerConnectionString,
        string? readerPassword = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(readerConnectionString);

        var connectionString = readerConnectionString;
        if (!string.IsNullOrEmpty(readerPassword))
        {
            // NpgsqlConnectionStringBuilder — not a raw NpgsqlConnection — so the credential is set
            // structurally rather than string-concatenated. The base string carries no password;
            // the secret is applied here and nowhere else.
            connectionString = new NpgsqlConnectionStringBuilder(readerConnectionString)
            {
                Password = readerPassword,
            }.ConnectionString;
        }

        // Building the data source does not open a connection, so a host with no database reachable
        // still boots — the first raw read is where an unreachable reader would surface.
        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();

        services.AddSingleton(dataSource);
        services.AddScoped<ScopedConnection>();

        return services;
    }
}
