using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Sales.Domain;

/// <summary>
/// A person at an account (DOMAIN.md §2): they belong to <em>exactly one</em> account. That rule is not
/// merely modelled — it is structural. A contact can only be constructed from its parent
/// <see cref="Account"/>, from which it copies the tenant and all five scope facts; there is no
/// constructor that lets a caller name a second account or hand-pick a scope column.
/// <para>
/// A person who moves is a <b>new</b> contact; the old one is marked <see cref="IsDeparted">departed</see>,
/// never row-deleted, so history stays attributable (DOMAIN.md §2). There is deliberately no delete path.
/// </para>
/// <para>
/// Like <see cref="Account"/>, the contact is both <see cref="ITenantOwned"/> (so
/// <see cref="Persistence.SalesDbContext"/> filters it by tenant) and <see cref="IScopedResource"/> (so
/// both scope paths read its ownership facts from the row). The five scope columns are denormalised —
/// inherited from the account at create time — because the single-table RLS <c>USING</c> clause and the
/// EF predicate both read them from the row, and neither can express a join to the account (plan design
/// decision).
/// </para>
/// </summary>
public sealed class Contact : ITenantOwned, IScopedResource
{
    private Contact()
    {
    }

    /// <summary>
    /// Creates a contact under <paramref name="account"/>. Every scope fact — tenant, owner, team, region
    /// and the account id itself — is taken from the account, never from caller input: attaching a contact
    /// to an account and inheriting that account's scope are the same act. The caller supplies only the
    /// person's business fields.
    /// </summary>
    public Contact(
        Guid id,
        Account account,
        string name,
        string? email,
        string? phone,
        string? messenger)
    {
        ArgumentNullException.ThrowIfNull(account);

        Id = id;
        TenantId = account.TenantId;

        // The one-account rule and scope inheritance in one place: the contact belongs to this account and
        // inherits its owner/team/region. account_id is the parent's own id (an account carries
        // account_id = id), so a DataScope.Account(acc) grant admits the account and its contacts alike.
        AccountId = account.Id;
        OwnerUserId = account.OwnerUserId;
        TeamId = account.TeamId;
        RegionId = account.RegionId;

        Name = Require(name, nameof(name));
        Email = Trimmed(email);
        Phone = Trimmed(phone);
        Messenger = Trimmed(messenger);
        IsDeparted = false;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    /// <summary>
    /// The one account this contact belongs to. Immutable — a person who moves is a new contact. Typed
    /// <see cref="Nullable{Guid}"/> to match <see cref="IScopedResource.AccountId"/> so the EF scope
    /// predicate can be translated against this column; it is always set and mapped <c>IsRequired</c>, so
    /// the column is NOT NULL and the parent-account FK is enforced.
    /// </summary>
    public Guid? AccountId { get; private set; }

    /// <summary>The owning agent, inherited from the account. Re-stamped by <see cref="Reinherit"/> when the
    /// account is reassigned (edge 8).</summary>
    public UserId OwnerUserId { get; private set; }

    public Guid? TeamId { get; private set; }

    public Guid? RegionId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public string? Messenger { get; private set; }

    /// <summary>True once the person has left. A departed contact is kept for history; it never becomes a
    /// deleted row (DOMAIN.md §2) and is excluded from active lists.</summary>
    public bool IsDeparted { get; private set; }

    public DateTimeOffset? DepartedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Marks the person as departed. Idempotent — departing an already-departed contact is a no-op that
    /// keeps the original timestamp — because "remove" is a state, not an event, and a double-submit must
    /// not rewrite history. The row is never deleted; this is the only "removal".
    /// </summary>
    public void Depart()
    {
        if (IsDeparted)
        {
            return;
        }

        IsDeparted = true;
        DepartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Re-stamps the inherited scope columns (owner, team, region) from <paramref name="account"/> after
    /// that account is reassigned (edge 8). <see cref="TenantId"/> and <see cref="AccountId"/> are
    /// immutable and deliberately not touched — a contact does not change tenant or parent, only the
    /// owner / team / region it inherits. Called by <see cref="Application.AccountService"/> in the same
    /// unit of work as the account edit, so a reassignment can never leave a contact visible under a
    /// stale grant.
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

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
