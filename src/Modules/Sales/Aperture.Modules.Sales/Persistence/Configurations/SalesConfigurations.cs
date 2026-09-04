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
        // Nullable CLR property (to match IScopedResource so the EF scope predicate translates), but the
        // column is NOT NULL — an account always carries account_id = id.
        builder.Property(a => a.AccountId).HasColumnName("account_id").IsRequired();
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

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("contacts", SalesDbContext.Schema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(TypedIdConverters.Tenant);
        // Nullable CLR property (to match IScopedResource so the EF scope predicate translates); mapped
        // IsRequired so the column is NOT NULL and the one-account FK below is enforced.
        builder.Property(c => c.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(c => c.OwnerUserId).HasColumnName("owner_user_id").HasConversion(TypedIdConverters.User);
        builder.Property(c => c.TeamId).HasColumnName("team_id");
        builder.Property(c => c.RegionId).HasColumnName("region_id");
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(c => c.Email).HasColumnName("email").HasMaxLength(320);
        builder.Property(c => c.Phone).HasColumnName("phone").HasMaxLength(64);
        builder.Property(c => c.Messenger).HasColumnName("messenger").HasMaxLength(200);
        builder.Property(c => c.IsDeparted).HasColumnName("is_departed");
        builder.Property(c => c.DepartedAt).HasColumnName("departed_at");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");

        // The parent-account FK. It enforces the one-account rule at the database: a contact whose
        // account_id names no account cannot commit. account_id doubles as the scope column (a contact's
        // account is its own account), so this single column carries both the relationship and the scope.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(c => c.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Serves the active-contacts grid ordering — (created_at, id) keyset paging — with the tenant term
        // leading the scope predicate. is_departed is included so the active filter is covered.
        builder.HasIndex(c => new { c.TenantId, c.CreatedAt, c.Id });
    }
}
