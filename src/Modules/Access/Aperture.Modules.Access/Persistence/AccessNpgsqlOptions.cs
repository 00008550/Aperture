using Microsoft.EntityFrameworkCore;

namespace Aperture.Modules.Access.Persistence;

/// <summary>
/// The provider configuration, in exactly one place.
/// <para>
/// It lives here because the design-time factory and the runtime registration must agree. They
/// did not, briefly: the module put the migrations history in <c>access.__migrations</c> while
/// <c>dotnet ef</c> wrote to the default <c>__EFMigrationsHistory</c>, so a migration applied by
/// the CLI was invisible to the application and would have been applied a second time on
/// startup.
/// </para>
/// </summary>
internal static class AccessNpgsqlOptions
{
    public static TBuilder UseAccessNpgsql<TBuilder>(
        this TBuilder builder,
        string connectionString)
        where TBuilder : DbContextOptionsBuilder =>
        (TBuilder)
        builder.UseNpgsql(connectionString, npgsql => npgsql
            // History in the module's own schema: a shared history table is a cross-module
            // coupling that only surfaces the first time two modules migrate at once.
            .MigrationsHistoryTable("__migrations", AccessDbContext.Schema)
            .MigrationsAssembly(typeof(AccessDbContext).Assembly.FullName));
}
