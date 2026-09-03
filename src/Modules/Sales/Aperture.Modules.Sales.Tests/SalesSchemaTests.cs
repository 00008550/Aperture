using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aperture.Modules.Sales.Tests;

/// <summary>
/// P1's schema bootstrap, proven against a real PostgreSQL with the real Sales migration applied: the
/// <c>sales</c> schema exists and its migrations history is the module's own <c>sales.__migrations</c>,
/// not the shared default. This is the same regression guard Access carries — a design-time/runtime
/// mismatch on the history table location would apply a migration twice or silently never.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SalesSchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_migration_creates_the_sales_schema()
    {
        await using var db = postgres.CreateContext(TenantId.New());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'sales'",
            connection);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task The_history_table_is_the_modules_own_not_the_shared_default()
    {
        await using var db = postgres.CreateContext(TenantId.New());
        _ = await db.Database.GetAppliedMigrationsAsync();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'sales' AND table_name = '__migrations'
            """,
            connection);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }
}
