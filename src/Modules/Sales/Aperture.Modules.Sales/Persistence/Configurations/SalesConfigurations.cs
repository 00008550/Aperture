using Aperture.Modules.Sales.Domain;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aperture.Modules.Sales.Persistence.Configurations;

/// <summary>
/// Converters for the typed ids, stored as plain <c>uuid</c> so the strong typing costs nothing in the
/// database and the columns stay joinable from psql. A verbatim mirror of Access's converters — the
/// module owns its own copy rather than sharing one, because sharing it would be a cross-module coupling
/// (ARCHITECTURE.md §1).
/// </summary>
internal static class TypedIdConverters
{
    public static readonly ValueConverter<TenantId, Guid> Tenant =
        new(id => id.Value, value => new TenantId(value));

    public static readonly ValueConverter<UserId, Guid> User =
        new(id => id.Value, value => new UserId(value));
}

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", SalesDbContext.Schema);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id");
        builder.Property(a => a.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        builder.Property(a => a.OwnerUserId).HasColumnName("owner_user_id").HasConversion(TypedIdConverters.User);
        builder.Property(a => a.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(a => a.TaxId).HasColumnName("tax_id").HasMaxLength(64).IsRequired();
        builder.Property(a => a.CreditLimit).HasColumnName("credit_limit").HasColumnType("numeric(18,2)");
        builder.Property(a => a.PaymentTermsDays).HasColumnName("payment_terms_days");
        builder.Property(a => a.RegionId).HasColumnName("region_id");
        builder.Property(a => a.TeamId).HasColumnName("team_id");
        builder.Property(a => a.AccountId).HasColumnName("account_id");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");

        // xmin as the optimistic concurrency token (ARCHITECTURE.md §5). Npgsql maps the system column;
        // EF reloads it after each write, so a stale token on the next update fails the check and 409s.
        builder.Property(a => a.Version).HasColumnName("xmin").IsRowVersion();

        // Tax-identifier dedup, per tenant (DOMAIN.md §2). The same company arriving twice in one tenant
        // collides here rather than becoming a second row; the same tax id in another tenant is a
        // distinct account, because the index is composite. This is also the cheapest correct
        // idempotency for account creation — a double-submit conflicts instead of duplicating.
        builder.HasIndex(a => new { a.TenantId, a.TaxId }).IsUnique();

        // Keyset pagination reads the grid ordered by (created_at, id); this index serves that ordering
        // and the scope predicate's tenant term as its leading column.
        builder.HasIndex(a => new { a.TenantId, a.CreatedAt, a.Id });
    }
}
