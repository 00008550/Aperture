using Aperture.SharedKernel.Authorization;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Sales.Application;

/// <summary>
/// What a caller supplies to create a contact. Note what is <em>absent</em>: no account id (it comes from
/// the route, and is validated to be in the caller's scope), and none of the five scope columns — those
/// are inherited from the parent account by the service, never named by the caller. Only the person's
/// business fields are the caller's to state.
/// </summary>
public sealed record CreateContactRequest(
    string Name,
    string? Email,
    string? Phone,
    string? Messenger);

/// <summary>The read model for one contact — the shape the console and the assistant read.</summary>
public sealed record ContactView(
    Guid Id,
    Guid TenantId,
    Guid AccountId,
    Guid OwnerUserId,
    Guid? TeamId,
    Guid? RegionId,
    string Name,
    string? Email,
    string? Phone,
    string? Messenger,
    bool IsDeparted,
    DateTimeOffset? DepartedAt,
    DateTimeOffset CreatedAt);

public enum ContactCreateStatus
{
    Created = 1,

    /// <summary>No account with that id is visible to the caller's tenant and scope. A contact cannot be
    /// attached to an account the caller cannot see, and a cross-tenant account reference fails here.</summary>
    AccountNotFound = 2,
}

public sealed record ContactCreateResult(ContactCreateStatus Status, ContactView? Contact);

public enum ContactDepartStatus
{
    Departed = 1,

    /// <summary>No contact with that id is visible to the caller's tenant and scope.</summary>
    NotFound = 2,
}

public sealed record ContactDepartResult(ContactDepartStatus Status, ContactView? Contact);

/// <summary>One page of the contacts grid, plus the cursor that fetches the next page (null at the end).</summary>
public sealed record ContactsPage(IReadOnlyList<ContactView> Items, string? NextCursor);

/// <summary>
/// The Sales module's public surface for contacts. The implementation is internal; the endpoint host
/// reaches it only through this interface (ARCHITECTURE.md §1). Every method takes the caller's resolved
/// identity and scopes explicitly — the service never invents them.
/// </summary>
public interface IContactService
{
    /// <summary>
    /// Creates a contact under the account named by <paramref name="accountId"/>, which is validated to
    /// exist and to be within the caller's scope before anything is written. The contact inherits the
    /// account's tenant and five scope facts.
    /// </summary>
    Task<ContactCreateResult> CreateAsync(
        DataScopeSet scopes,
        Guid accountId,
        CreateContactRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the contact departed (never deletes it), if the caller's scope admits it.</summary>
    Task<ContactDepartResult> DepartAsync(
        DataScopeSet scopes,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The contacts grid, scoped through the reader role and its row-security policy: keyset-paginated by
    /// <c>(created_at, id)</c>, returning only rows the caller's scope admits. Active contacts by default;
    /// <paramref name="includeDeparted"/> also returns the departed rows kept for history.
    /// </summary>
    Task<ContactsPage> ListAsync(
        DataScopeSet scopes,
        bool includeDeparted,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);
}
