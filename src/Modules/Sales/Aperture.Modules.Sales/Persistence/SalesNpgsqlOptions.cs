using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Sales.Persistence;

/// <summary>
/// The provider configuration, in exactly one place — so the design-time factory and the runtime
/// registration cannot drift on where the migrations history lives. The history table is the module's
/// own <c>sales.__migrations</c>, never the shared default: a shared history table is a cross-module
/// coupling that only surfaces the first time two modules migrate at once.
/// </summary>
internal static class SalesNpgsqlOptions
{
    public static TBuilder UseSalesNpgsql<TBuilder>(
        this TBuilder builder,
        string connectionString)
        where TBuilder : DbContextOptionsBuilder =>
        (TBuilder)
        builder.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsHistoryTable("__migrations", SalesDbContext.Schema)
            .MigrationsAssembly(typeof(SalesDbContext).Assembly.FullName));
}
