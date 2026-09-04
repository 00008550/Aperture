using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Sales.Domain;

/// <summary>
/// An intent to sell, owned by one agent (DOMAIN.md §2). A deal belongs to exactly one
/// <see cref="Account"/> and, like <see cref="Contact"/>, inherits its tenant and all five scope facts
/// from that account at create time — there is no constructor that lets a caller name a tenant, an owner,
/// or a second account. It is the aggregate root that owns its <see cref="DealLine"/>s: lines are loaded
/// and saved with the deal, because the domain rules (a won deal needs a priced line; a quote freezes the
/// price-list version) are evaluated over them.
/// <para>
/// P4 builds the aggregate, its creation and its scoped grid only — the deal starts in
/// <see cref="Stages.New"/> and there are no transitions yet (the table-driven state machine is P5). The
/// wider deal columns (the frozen price-list version, the discount pending-approval fields and the lost
/// reason code) are present on the row from P4 so P5/P6 add behaviour without a follow-on migration
/// (plan target design), but nothing mutates them here.
/// </para>
/// <para>
/// Like <see cref="Account"/> and <see cref="Contact"/>, the deal is both <see cref="ITenantOwned"/> (so
/// <see cref="Persistence.SalesDbContext"/> filters it by tenant) and <see cref="IScopedResource"/> (so
/// both scope paths read its ownership facts from the row). The five scope columns are denormalised —
/// inherited from the account, and re-stamped by <see cref="Reinherit"/> when the account is reassigned
/// (edge 8) — because the single-table RLS <c>USING</c> clause and the EF predicate both read them from
/// the row and neither can express a join to the account.
/// </para>
/// </summary>
public sealed class Deal : ITenantOwned, IScopedResource
{
    /// <summary>The fixed deal lifecycle stages (DOMAIN.md §2). P4 only ever writes <see cref="New"/>;
    /// the transitions between these live in P5's table-driven state machine.</summary>
    public static class Stages
    {
        public const string New = "new";
        public const string Qualified = "qualified";
        public const string Quoted = "quoted";
        public const string Negotiation = "negotiation";
        public const string Won = "won";
        public const string Lost = "lost";
    }

    private readonly List<DealLine> _lines = new();

    private Deal()
    {
    }

    /// <summary>
    /// Creates a deal under <paramref name="account"/>. Every scope fact — tenant, owner, team, region and
    /// the account id itself — is taken from the account, never from caller input, exactly as
    /// <see cref="Contact"/>. The caller supplies only the deal's business fields; the deal opens in
    /// <see cref="Stages.New"/>.
    /// </summary>
    public Deal(
        Guid id,
        Account account,
        string name,
        decimal amount,
        decimal discountPct)
    {
        ArgumentNullException.ThrowIfNull(account);

        Id = id;
        TenantId = account.TenantId;

        // The one-account rule and scope inheritance in one place: the deal belongs to this account and
        // inherits its owner/team/region. account_id is the parent's own id (an account carries
        // account_id = id), so a DataScope.Account(acc) grant admits the account, its contacts and its
        // deals alike.
        AccountId = account.Id;
        OwnerUserId = account.OwnerUserId;
        TeamId = account.TeamId;
        RegionId = account.RegionId;

        Name = Require(name, nameof(name));
        Amount = NonNegative(amount, nameof(amount));
        DiscountPct = Percentage(discountPct, nameof(discountPct));
        Stage = Stages.New;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    /// <summary>
    /// The one account this deal belongs to. Immutable. Typed <see cref="Nullable{Guid}"/> to match
    /// <see cref="IScopedResource.AccountId"/> so the EF scope predicate translates against this column;
    /// it is always set and mapped <c>IsRequired</c>, so the column is NOT NULL and the parent FK holds.
    /// </summary>
    public Guid? AccountId { get; private set; }

    /// <summary>The owning agent, inherited from the account. Re-stamped by <see cref="Reinherit"/> when the
    /// account is reassigned (edge 8).</summary>
    public UserId OwnerUserId { get; private set; }

    public Guid? TeamId { get; private set; }

    public Guid? RegionId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>The deal's current lifecycle stage. <see cref="Stages.New"/> on create; only P5's state
    /// machine advances it.</summary>
    public string Stage { get; private set; } = Stages.New;

    public decimal Amount { get; private set; }

    public decimal DiscountPct { get; private set; }

    /// <summary>The price-list version frozen when the deal reaches <c>quoted</c> (DOMAIN.md §2 rule 2).
    /// Null until P5 freezes it; carried on the row from P4 to avoid a follow-on migration.</summary>
    public string? FrozenPriceListVersion { get; private set; }

    /// <summary>True while a discount above the agent's threshold awaits a lead's approval (DOMAIN.md §2
    /// rule 3). Set by P6; false and untouched in P4.</summary>
    public bool PendingApproval { get; private set; }

    /// <summary>The discount percentage awaiting approval, when <see cref="PendingApproval"/> is set.
    /// Carried on the row from P4; written by P6.</summary>
    public decimal? PendingApprovalDiscountPct { get; private set; }

    /// <summary>The reason code recorded when a deal is lost (DOMAIN.md §2 rule 4). Carried from P4;
    /// required and written by P5's <c>lost</c> transition.</summary>
    public string? LostReasonCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// The PostgreSQL <c>xmin</c> system column, mapped as the optimistic concurrency token (as on
    /// <see cref="Account"/>): two writers editing the same deal — the contended case P5's transitions
    /// hit — cannot both win. Never set by the application.
    /// </summary>
    public uint Version { get; private set; }

    /// <summary>The deal's lines, loaded and saved with the aggregate. Read-only to callers; a line is
    /// added only through <see cref="AddLine"/>.</summary>
    public IReadOnlyList<DealLine> Lines => _lines;

    /// <summary>
    /// Adds a line (a product, a unit price, a quantity, and the price-list version it was priced against)
    /// to the deal. The line inherits the deal's tenant, so it cannot be constructed apart from its parent.
    /// </summary>
    public DealLine AddLine(string productRef, decimal unitPrice, int quantity, string? priceListVersion)
    {
        var line = new DealLine(Guid.NewGuid(), this, productRef, unitPrice, quantity, priceListVersion);
        _lines.Add(line);
        return line;
    }

    /// <summary>
    /// Moves the deal to <paramref name="to"/> if the <see cref="DealStateMachine"/> allows it. The machine
    /// decides — is this a legal edge, and does its guard pass — and only on a
    /// <see cref="DealTransitionStatus.Transitioned"/> verdict does the deal apply the effect: the stage
    /// advances, a move to <c>quoted</c> freezes the price-list version onto the deal and every line
    /// (rule 2), and a move to <c>lost</c> records the reason (rule 4). An illegal edge (including any move
    /// out of the terminal <c>won</c>/<c>lost</c>) or a failed guard changes nothing and is reported through
    /// the returned <see cref="DealTransitionResult"/> — a domain outcome, never an exception. The result
    /// always carries the from/to stages so the caller can audit the attempt whether or not it succeeded.
    /// </summary>
    public DealTransitionResult Transition(string to, DealTransitionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var from = Stage;
        var status = DealStateMachine.Evaluate(this, to, input);
        if (status != DealTransitionStatus.Transitioned)
        {
            return new DealTransitionResult(status, from, to);
        }

        // The effect of a legal, guarded transition. Only the aggregate root touches its own state and its
        // lines'; the machine decided, this applies.
        switch (to)
        {
            case Stages.Quoted:
                // Freeze the version onto the deal and every line, so a later price-list change cannot alter
                // this outstanding quote (rule 2). Trimmed non-empty is guaranteed by the guard.
                var frozen = input.PriceListVersion!.Trim();
                FrozenPriceListVersion = frozen;
                foreach (var line in _lines)
                {
                    line.Freeze(frozen);
                }

                break;

            case Stages.Lost:
                LostReasonCode = input.Reason!.Trim();
                break;
        }

        Stage = to;
        return new DealTransitionResult(DealTransitionStatus.Transitioned, from, to);
    }

    /// <summary>
    /// Re-stamps the inherited scope columns (owner, team, region) from <paramref name="account"/> after
    /// that account is reassigned (edge 8). <see cref="TenantId"/> and <see cref="AccountId"/> are
    /// immutable and deliberately not touched — a deal does not change tenant or parent, only the owner /
    /// team / region it inherits. Called by <see cref="Application.AccountService"/> in the same unit of
    /// work as the account edit, so a reassignment can never leave a deal visible under a stale grant.
    /// </summary>
    public void Reinherit(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        OwnerUserId = account.OwnerUserId;
        TeamId = account.TeamId;
        RegionId = account.RegionId;
    }

    private static string Require(string value, string paramName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value.Trim();

    private static decimal NonNegative(decimal value, string paramName) =>
        value < 0 ? throw new ArgumentOutOfRangeException(paramName, value, "Must not be negative.") : value;

    private static decimal Percentage(decimal value, string paramName) =>
        value is < 0 or > 100
            ? throw new ArgumentOutOfRangeException(paramName, value, "Must be between 0 and 100.")
            : value;
}
