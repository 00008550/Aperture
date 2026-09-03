using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Sales.Application;

/// <summary>
/// What a caller supplies to create an account. Note what is <em>absent</em>: the tenant and the owning
/// agent are stamped from the request principal by the service, never read from here — a caller naming
/// its own tenant or owner is exactly how a row lands in the wrong place. The region and team are
/// business attributes of the account (which region it belongs to), so they are the caller's to state.
/// </summary>
public sealed record CreateAccountRequest(
    string Name,
    string TaxId,
    decimal CreditLimit,
    int PaymentTermsDays,
    Guid? RegionId,
    Guid? TeamId);

/// <summary>
/// An edit to an existing account. <see cref="ExpectedVersion"/> is the <c>xmin</c> the caller last read;
/// a mismatch means someone else edited the row first and the update is a 409 rather than a lost update.
/// <see cref="OwnerUserId"/> reassigns the account to a different agent — a deliberate write, unlike
/// create where the owner is always the caller.
/// </summary>
public sealed record UpdateAccountRequest(
    Guid OwnerUserId,
    string Name,
    decimal CreditLimit,
    int PaymentTermsDays,
    Guid? RegionId,
    Guid? TeamId,
    uint ExpectedVersion);

/// <summary>The read model for one account — the shape the console and the assistant read.</summary>
public sealed record AccountView(
    Guid Id,
    Guid TenantId,
    Guid OwnerUserId,
    string Name,
    string TaxId,
    decimal CreditLimit,
    int PaymentTermsDays,
    Guid? RegionId,
    Guid? TeamId,
    Guid AccountId,
    DateTimeOffset CreatedAt,
    uint Version);

public enum AccountCreateStatus
{
    Created = 1,
    DuplicateTaxId = 2,
}

/// <summary>The outcome of a create: a new account, or a duplicate rejected by the tax-id unique index.</summary>
public sealed record AccountCreateResult(AccountCreateStatus Status, AccountView? Account);

public enum AccountUpdateStatus
{
    Updated = 1,

    /// <summary>No account with that id is visible to the caller's tenant and scope.</summary>
    NotFound = 2,

    /// <summary>The account changed since the caller read it (xmin mismatch).</summary>
    Conflict = 3,
}

public sealed record AccountUpdateResult(AccountUpdateStatus Status, AccountView? Account);

/// <summary>One page of the accounts grid, plus the cursor that fetches the next page (null at the end).</summary>
public sealed record AccountsPage(IReadOnlyList<AccountView> Items, string? NextCursor);

/// <summary>
/// The Sales module's public surface for accounts. The implementation is internal; the endpoint host
/// reaches it only through this interface, so the module boundary stays real (ARCHITECTURE.md §1).
/// Every method takes the caller's resolved identity and scopes explicitly — the service never invents
/// them.
/// </summary>
public interface IAccountService
{
    Task<AccountCreateResult> CreateAsync(
        TenantId tenant,
        UserId owner,
        CreateAccountRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>One account by id, if the caller's tenant and data scope admit it; otherwise null.</summary>
    Task<AccountView?> GetAsync(
        Aperture.SharedKernel.Authorization.DataScopeSet scopes,
        Guid id,
        CancellationToken cancellationToken = default);

    Task<AccountUpdateResult> UpdateAsync(
        Aperture.SharedKernel.Authorization.DataScopeSet scopes,
        Guid id,
        UpdateAccountRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The accounts grid, scoped through the reader role and its row-security policy: keyset-paginated by
    /// <c>(created_at, id)</c>, returning only rows the caller's scope admits.
    /// </summary>
    Task<AccountsPage> ListAsync(
        Aperture.SharedKernel.Authorization.DataScopeSet scopes,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);
}
