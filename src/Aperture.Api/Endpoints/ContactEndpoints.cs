using Aperture.Api.Authentication;
using Aperture.Api.Authorization;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;

namespace Aperture.Api.Endpoints;

/// <summary>
/// The contacts HTTP surface (plan 002-P3). Every route carries a permission the moment it is mapped
/// (CLAUDE.md invariant 4): the write routes require <c>contacts.write</c>, the read route
/// <c>contacts.read</c>. Row-level scope is enforced below this, by the service's two sanctioned paths —
/// the endpoint only passes the resolved principal's scopes through, never anything the caller named.
/// The parent account id comes from the route and is validated to be in the caller's scope; there is no
/// hard-delete route — "removing" a contact is <c>POST …/depart</c>, which marks the row departed.
/// </summary>
public static class ContactEndpoints
{
    public static IEndpointRouteBuilder MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/accounts/{accountId:guid}/contacts", CreateContact)
            .RequirePermission(Permissions.ContactsWrite)
            .WithName("CreateContact");

        app.MapGet("/api/contacts", ListContacts)
            .RequirePermission(Permissions.ContactsRead)
            .WithName("ListContacts");

        app.MapPost("/api/contacts/{id:guid}/depart", DepartContact)
            .RequirePermission(Permissions.ContactsWrite)
            .WithName("DepartContact");

        return app;
    }

    private static async Task<IResult> CreateContact(
        HttpContext http,
        Guid accountId,
        CreateContactRequest request,
        IContactService contacts,
        CancellationToken cancellationToken)
    {
        var principal = http.GetAccessPrincipal();

        // The parent account is validated against the caller's scope inside the service; tenant, owner and
        // the scope columns are inherited from it, never from the request body.
        var result = await contacts.CreateAsync(principal.Scopes, accountId, request, cancellationToken);

        return result.Status switch
        {
            ContactCreateStatus.Created =>
                Results.Created($"/api/contacts/{result.Contact!.Id}", result.Contact),
            ContactCreateStatus.AccountNotFound => Results.NotFound(
                new { error = "No account with this id is visible to you." }),
            _ => Results.Problem("Unexpected create outcome."),
        };
    }

    private static async Task<IResult> ListContacts(
        HttpContext http,
        IContactService contacts,
        CancellationToken cancellationToken,
        bool includeDeparted = false,
        int? limit = null,
        string? cursor = null)
    {
        var principal = http.GetAccessPrincipal();

        var page = await contacts.ListAsync(
            principal.Scopes, includeDeparted, limit ?? 0, cursor, cancellationToken);

        return Results.Ok(page);
    }

    private static async Task<IResult> DepartContact(
        HttpContext http,
        Guid id,
        IContactService contacts,
        CancellationToken cancellationToken)
    {
        var principal = http.GetAccessPrincipal();

        var result = await contacts.DepartAsync(principal.Scopes, id, cancellationToken);

        return result.Status switch
        {
            ContactDepartStatus.Departed => Results.Ok(result.Contact),
            ContactDepartStatus.NotFound => Results.NotFound(),
            _ => Results.Problem("Unexpected depart outcome."),
        };
    }
}
