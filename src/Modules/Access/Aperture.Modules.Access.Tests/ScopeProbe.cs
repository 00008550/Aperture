using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aperture.Modules.Access.Tests;

/// <summary>
/// A minimal scoped table, used only to prove that a <see cref="DataScopeSet"/> becomes real SQL
/// (001-P4).
/// <para>
/// It lives in the test project, in its own schema, rather than in the Access module's migration:
/// no module owns a generic "scoped row", and adding a table to production migrations that only
/// tests read would be a permanent cost for a temporary need. The parts that matter for the
/// translation — value-converted typed ids, nullable scope columns, snake-cased columns, the real
/// Npgsql provider — are identical to a real entity.
/// </para>
/// </summary>
public sealed class ScopedRow : IScopedResource
{
    public Guid Id { get; set; }

    public TenantId TenantId { get; set; }

    public UserId OwnerUserId { get; set; }

    public Guid? TeamId { get; set; }

    public Guid? RegionId { get; set; }

    public Guid? AccountId { get; set; }
}

public sealed class ScopeProbeDbContext(DbContextOptions<ScopeProbeDbContext> options) : DbContext(options)
{
    public const string Schema = "scope_probe";

    public DbSet<ScopedRow> Rows => Set<ScopedRow>();

    /// <summary>
    /// The table, created directly rather than through a migration. The test project owns this
    /// schema entirely, so there is no deployment story to preserve.
    /// </summary>
    public const string CreateTableSql =
        $"""
         CREATE SCHEMA IF NOT EXISTS {Schema};
         CREATE TABLE IF NOT EXISTS {Schema}.rows (
             id uuid PRIMARY KEY,
             tenant_id uuid NOT NULL,
             owner_user_id uuid NOT NULL,
             team_id uuid NULL,
             region_id uuid NULL,
             account_id uuid NULL
         );
         """;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<ScopedRow>();
        row.ToTable("rows", Schema);
        row.HasKey(r => r.Id);
        row.Property(r => r.Id).HasColumnName("id");
        row.Property(r => r.TenantId).HasColumnName("tenant_id")
            .HasConversion(new ValueConverter<TenantId, Guid>(id => id.Value, value => new TenantId(value)));
        row.Property(r => r.OwnerUserId).HasColumnName("owner_user_id")
            .HasConversion(new ValueConverter<UserId, Guid>(id => id.Value, value => new UserId(value)));
        row.Property(r => r.TeamId).HasColumnName("team_id");
        row.Property(r => r.RegionId).HasColumnName("region_id");
        row.Property(r => r.AccountId).HasColumnName("account_id");
    }
}
