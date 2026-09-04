using System.Diagnostics;
using Aperture.Api.Authentication;
using Aperture.Api.Authorization;
using Aperture.Modules.Access.Auditing;
using Aperture.Modules.Access.Domain;
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

        app.MapPost("/api/deals/{id:guid}/transition", TransitionDeal)
            .RequirePermission(Permissions.DealsWrite)
            .WithName("TransitionDeal");

        app.MapPost("/api/deals/{id:guid}/approve-discount", ApproveDiscount)
            .RequirePermission(Permissions.DealsDiscountApprove)
            .WithName("ApproveDiscount");

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

    private static async Task<IResult> TransitionDeal(
        HttpContext http,
        Guid id,
        TransitionDealRequest request,
        IDealService deals,
        IAuditTrail audit,
        CancellationToken cancellationToken)
    {
        var principal = http.GetAccessPrincipal();

        // The deal is loaded and moved through the caller's scope inside the service; the state machine
        // (not this endpoint) decides whether the edge is legal and its rule guard passes.
        var result = await deals.TransitionAsync(principal.Scopes, id, request, cancellationToken);

        // One audit row per transition attempt (edge 11's persisted reason, edge 12's auditable illegal
        // attempt): who, from → to, and the reason, stamped with the ambient tenant. Written through the
        // Access trail — the composition root's job, the same seam that audits denials — after the Sales
        // write settles. It is NOT enrolled in the Sales unit of work: the two live in different module
        // schemas and contexts, and a cross-schema atomic write needs infrastructure this plan defers (see
        // the outbox deferral). Not-found and conflict are not recorded: the first names no real deal to
        // attribute a move to, and the second changed nothing.
        if (result.Outcome is not (DealTransitionOutcome.DealNotFound or DealTransitionOutcome.Conflict))
        {
            await audit.RecordAsync(
                new AuditEntry(AuditCategory.Mutation, AuditActor.KindFor(http), principal.UserId)
                {
                    Action = $"POST /api/deals/{id}/transition {result.FromStage}->{result.ToStage}",
                    Reason = result.Outcome switch
                    {
                        DealTransitionOutcome.Transitioned => request.Reason,
                        // Rule 3: the move held for a lead's approval — a real state change (pending recorded),
                        // audited as such rather than as a rejection.
                        DealTransitionOutcome.PendingApproval => "discount over threshold: pending approval",
                        _ => $"rejected: {result.Outcome}",
                    },
                    ScopeDecision = principal.Scopes.IsEmpty ? "no scopes" : "scoped",
                    CorrelationId = Activity.Current?.Id ?? http.TraceIdentifier,
                },
                cancellationToken);
        }

        return result.Outcome switch
        {
            DealTransitionOutcome.Transitioned => Results.Ok(result.Deal),
            // Rule 3: an over-threshold discount held the deal in negotiation with a pending approval. This is
            // an expected, successful outcome — 200 with the deal (PendingApproval set) so the caller can
            // route it to a lead; not an error the client should retry differently.
            DealTransitionOutcome.PendingApproval => Results.Ok(result.Deal),
            DealTransitionOutcome.DealNotFound => Results.NotFound(),
            // The optimistic-concurrency loss: 409 with the current state so the caller re-applies (edge 15).
            DealTransitionOutcome.Conflict => Results.Conflict(result.Deal),
            // Illegal/terminal edge and the three rule-guard failures are unprocessable domain errors (422).
            DealTransitionOutcome.IllegalTransition => Results.UnprocessableEntity(
                new { error = $"Cannot move a deal from {result.FromStage} to {result.ToStage}." }),
            DealTransitionOutcome.NoPricedLine => Results.UnprocessableEntity(
                new { error = "A deal can be won only with at least one line that has a price and a quantity." }),
            DealTransitionOutcome.ReasonRequired => Results.UnprocessableEntity(
                new { error = "A deal can be lost only with a reason code." }),
            DealTransitionOutcome.PriceListVersionRequired => Results.UnprocessableEntity(
                new { error = "Moving a deal to quoted requires a price-list version to freeze." }),
            _ => Results.Problem("Unexpected transition outcome."),
        };
    }

    private static async Task<IResult> ApproveDiscount(
        HttpContext http,
        Guid id,
        ApproveDiscountRequest request,
        IDealService deals,
        IAuditTrail audit,
        CancellationToken cancellationToken)
    {
        var principal = http.GetAccessPrincipal();

        // The why is required — an approval with no recorded reason is exactly the "no reason" gap DOMAIN.md
        // warns about for lost deals. Reject before touching state.
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.BadRequest(new { error = "A discount approval requires a reason." });
        }

        // Who may approve is enforced above by the deals.discount.approve policy (a caller without it never
        // reaches here — 403). The deal is still loaded through the caller's scope inside the service, so a
        // deal they cannot see cannot be approved.
        var result = await deals.ApproveDiscountAsync(principal.Scopes, id, request, cancellationToken);

        // Audit only the state-changing outcome (who + why), the same host-side seam the transitions use:
        // Access owns the audit schema and Sales cannot reach across the §1 boundary to write it, so the
        // composition root records it after the Sales write settles. Not-found, not-pending and conflict
        // changed nothing and are not recorded.
        if (result.Outcome == DealDiscountApprovalOutcome.Approved)
        {
            await audit.RecordAsync(
                new AuditEntry(AuditCategory.Mutation, AuditActor.KindFor(http), principal.UserId)
                {
                    Action = $"POST /api/deals/{id}/approve-discount",
                    Reason = request.Reason,
                    ScopeDecision = principal.Scopes.IsEmpty ? "no scopes" : "scoped",
                    CorrelationId = Activity.Current?.Id ?? http.TraceIdentifier,
                },
                cancellationToken);
        }

        return result.Outcome switch
        {
            DealDiscountApprovalOutcome.Approved => Results.Ok(result.Deal),
            DealDiscountApprovalOutcome.DealNotFound => Results.NotFound(),
            // Nothing to approve: the deal has no pending approval outstanding.
            DealDiscountApprovalOutcome.NotPending => Results.Conflict(
                new { error = "This deal has no pending discount approval." }),
            // Lost the optimistic-concurrency check: 409 with the current state so the lead re-reads (edge 15).
            DealDiscountApprovalOutcome.Conflict => Results.Conflict(result.Deal),
            _ => Results.Problem("Unexpected approval outcome."),
        };
    }
}
