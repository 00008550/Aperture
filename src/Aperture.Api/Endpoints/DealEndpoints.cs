using Aperture.Api.Authentication;
using Aperture.Api.Authorization;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;

namespace Aperture.Api.Endpoints;

/// <summary>
/// The deals HTTP surface (plan 002-P4). Every route carries a permission the moment it is mapped
/// (CLAUDE.md invariant 4): the write routes require <c>deals.write</c>, the read routes <c>deals.read</c>.
/// Row-level scope is enforced below this, by the service's two sanctioned paths — the endpoint only
/// passes the resolved principal's scopes through, never anything the caller named. The parent account is
/// named in the create body and validated to be in the caller's scope; the deal inherits its tenant and
/// scope from that account. P4 exposes create, single-deal read, add-line and the grid; the lifecycle
/// transitions (and their <c>deals.discount.approve</c> path) are P5/P6.
/// </summary>
public static class DealEndpoints
{
    public static IEndpointRouteBuilder MapDealEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/deals", CreateDeal)
            .RequirePermission(Permissions.DealsWrite)
            .WithName("CreateDeal");

        app.MapGet("/api/deals", ListDeals)
            .RequirePermission(Permissions.DealsRead)
            .WithName("ListDeals");

        app.MapGet("/api/deals/{id:guid}", GetDeal)
            .RequirePermission(Permissions.DealsRead)
            .WithName("GetDeal");

        app.MapPost("/api/deals/{id:guid}/lines", AddDealLine)
            .RequirePermission(Permissions.DealsWrite)
            .WithName("AddDealLine");

        return app;
    }

    private static async Task<IResult> CreateDeal(
        HttpContext http,
        CreateDealRequest request,
        IDealService deals,
        CancellationToken cancellationToken)
    {
        var principal = http.GetAccessPrincipal();

        // The parent account is validated against the caller's scope inside the service; tenant, owner and
        // the scope columns are inherited from it, never from the request body.
        var result = await deals.CreateAsync(principal.Scopes, request, cancellationToken);

        return result.Status switch
        {
            DealCreateStatus.Created =>
                Results.Created($"/api/deals/{result.Deal!.Id}", result.Deal),
            DealCreateStatus.AccountNotFound => Results.NotFound(
                new { error = "No account with this id is visible to you." }),
            _ => Results.Problem("Unexpected create outcome."),
        };
    }

    private static async Task<IResult> ListDeals(
        HttpContext http,
        IDealService deals,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null)
    {
        var principal = http.GetAccessPrincipal();

        var page = await deals.ListAsync(principal.Scopes, limit ?? 0, cursor, cancellationToken);

        return Results.Ok(page);
    }

    private static async Task<IResult> GetDeal(
        HttpContext http,
        Guid id,
        IDealService deals,
        CancellationToken cancellationToken)
    {
        var principal = http.GetAccessPrincipal();

        var deal = await deals.GetAsync(principal.Scopes, id, cancellationToken);

        return deal is null ? Results.NotFound() : Results.Ok(deal);
    }

    private static async Task<IResult> AddDealLine(
        HttpContext http,
        Guid id,
        AddDealLineRequest request,
        IDealService deals,
        CancellationToken cancellationToken)
    {
        var principal = http.GetAccessPrincipal();

        var result = await deals.AddLineAsync(principal.Scopes, id, request, cancellationToken);

        return result.Status switch
        {
            DealLineAddStatus.Added => Results.Ok(result.Deal),
            DealLineAddStatus.DealNotFound => Results.NotFound(),
            _ => Results.Problem("Unexpected add-line outcome."),
        };
    }
}
