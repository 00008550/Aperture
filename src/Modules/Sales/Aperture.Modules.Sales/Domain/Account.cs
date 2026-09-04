using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Sales.Domain;

/// <summary>
/// A company we sell to (DOMAIN.md §2): a credit limit, payment terms, an owning agent, a region. It is
/// deduplicated on its tax identifier — the same company arriving twice is one account — enforced by a
/// unique <c>(tenant_id, tax_id)</c> index rather than a lookup-then-insert, so two concurrent creates
/// cannot both win.
/// <para>
/// The account is both <see cref="ITenantOwned"/> (so <see cref="Persistence.SalesDbContext"/> filters
/// it by tenant) and <see cref="IScopedResource"/> (so the row's ownership facts drive both scope
/// paths). The five scope columns are denormalised onto the row because both the EF predicate and the
/// single-table RLS <c>USING</c> clause read them <em>from the row</em> (design decision in the plan);
/// an account carries <see cref="AccountId"/> equal to its own id, so a <c>DataScope.Account(x)</c>
/// grant admits account <c>x</c> uniformly with its children.
/// </para>
/// </summary>
public sealed class Account : ITenantOwned, IScopedResource
{
    private Account()
    {
    }

    public Account(
        Guid id,
        TenantId tenantId,
        UserId ownerUserId,
        string name,
        string taxId,
        decimal creditLimit,
        int paymentTermsDays,
        Guid? regionId,
        Guid? teamId)
    {
        Id = id;
        TenantId = tenantId;
        OwnerUserId = ownerUserId;
        Name = Require(name, nameof(name));
        TaxId = Require(taxId, nameof(taxId));
        CreditLimit = NonNegative(creditLimit, nameof(creditLimit));
        PaymentTermsDays = NonNegative(paymentTermsDays, nameof(paymentTermsDays));
        RegionId = regionId;
        TeamId = teamId;

        // The account is its own scope target: account_id = id. This is a real, stored column and not a
        // computed alias because the RLS USING predicate is single-table and reads the column directly.
        AccountId = id;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    /// <summary>The owning agent. Stamped from the request principal on create, never from caller input.</summary>
    public UserId OwnerUserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string TaxId { get; private set; } = string.Empty;

    public decimal CreditLimit { get; private set; }

    public int PaymentTermsDays { get; private set; }

    public Guid? RegionId { get; private set; }

    public Guid? TeamId { get; private set; }

    /// <summary>
    /// Equal to <see cref="Id"/>: the account is its own scope target (see the class remarks). Typed
    /// <see cref="Nullable{Guid}"/> to match <see cref="IScopedResource.AccountId"/> so the EF scope
    /// predicate (which lifts to a nullable comparison, <c>account_id = @p</c> with NULL narrowing) can be
    /// translated against this column; it is always set and mapped <c>IsRequired</c>, so the column is
    /// NOT NULL.
    /// </summary>
    public Guid? AccountId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// The PostgreSQL <c>xmin</c> system column, mapped as the optimistic concurrency token. A second
    /// writer editing the same row after the first commits fails the token check and gets a 409, rather
    /// than silently overwriting (ARCHITECTURE.md §5). Never set by the application.
    /// </summary>
    public uint Version { get; private set; }

    /// <summary>
    /// Applies an edit to the account's business fields and its scope assignment (owner, region, team).
    /// Reassigning the owner or region is a deliberate <c>accounts.write</c> action, distinct from the
    /// tenant and id which are immutable. In P2 an account has no children; re-stamping contacts and
    /// deals on reassignment arrives with those aggregates (P3/P4, edge 8).
    /// </summary>
    public void Update(
        UserId ownerUserId,
        string name,
        decimal creditLimit,
        int paymentTermsDays,
        Guid? regionId,
        Guid? teamId)
    {
        OwnerUserId = ownerUserId;
        Name = Require(name, nameof(name));
        CreditLimit = NonNegative(creditLimit, nameof(creditLimit));
        PaymentTermsDays = NonNegative(paymentTermsDays, nameof(paymentTermsDays));
        RegionId = regionId;
        TeamId = teamId;
    }

    private static string Require(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value.Trim();

    private static decimal NonNegative(decimal value, string paramName) =>
        value < 0 ? throw new ArgumentOutOfRangeException(paramName, value, "Must not be negative.") : value;

    private static int NonNegative(int value, string paramName) =>
        value < 0 ? throw new ArgumentOutOfRangeException(paramName, value, "Must not be negative.") : value;
}
