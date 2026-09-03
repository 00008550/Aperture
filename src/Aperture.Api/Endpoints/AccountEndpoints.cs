using Aperture.Api.Authentication;
using Aperture.Api.Authorization;
using Aperture.Modules.Sales.Application;
using Aperture.SharedKernel.Authorization;

namespace Aperture.Api.Endpoints;

/// <summary>
/// The accounts HTTP surface (plan 002-P2). Every route carries a permission the moment it is mapped
/// (CLAUDE.md invariant 4): the write routes require <c>accounts.write</c>, the read routes
/// <c>accounts.read</c>. Row-level scope is enforced below this, by the service's two sanctioned paths —
/// the endpoint only passes the resolved principal's tenant, identity and scopes through, never anything
/// the caller named.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/accounts", CreateAccount)
            .RequirePermission(Permissions.AccountsWrite)
            .WithName("CreateAccount");

        app.MapGet("/api/accounts", ListAccounts)
            .RequirePermission(Permissions.AccountsRead)
            .WithName("ListAccounts");

        app.MapGet("/api/accounts/{id:guid}", GetAccount)
            .RequirePermission(Permissions.AccountsRead)
            .WithName("GetAccount");

        app.MapPatch("/api/accounts/{id:guid}", UpdateAccount)
            .RequirePermission(Permissions.AccountsWrite)
            .WithName("UpdateAccount");

        return app;
    }

    private static async Task<IResult> CreateAccount(
        HttpContext http,
        CreateAccountRequest request,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        var principal = http.GetAccessPrincipal();

        // Tenant and owner come from the resolved principal, never from the request body.
        var result = await accounts.CreateAsync(
            principal.TenantId, principal.UserId, request, cancellationToken);

        return result.Status switch
        {
            AccountCreateStatus.Created =>
                Results.Created($"/api/accounts/{result.Account!.Id}", result.Account),
            AccountCreateStatus.DuplicateTaxId =>
                Results.Conflict(new { error = "An account with this tax identifier already exists." }),
            _ => Results.Problem("Unexpected create outcome."),
        };
    }

    private static async Task<IResult> ListAccounts(
        HttpContext http,
        IAccountService accounts,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null)
    {
        var principal = http.GetAccessPrincipal();

        var page = await accounts.ListAsync(
            principal.Scopes, limit ?? 0, cursor, cancellationToken);

        return Results.Ok(page);
    }

    private static async Task<IResult> GetAccount(
        HttpContext http,
        Guid id,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        var principal = http.GetAccessPrincipal();

        var account = await accounts.GetAsync(principal.Scopes, id, cancellationToken);

        return account is null ? Results.NotFound() : Results.Ok(account);
    }

    private static async Task<IResult> UpdateAccount(
        HttpContext http,
        Guid id,
        UpdateAccountRequest request,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        var principal = http.GetAccessPrincipal();

        var result = await accounts.UpdateAsync(principal.Scopes, id, request, cancellationToken);

        return result.Status switch
        {
            AccountUpdateStatus.Updated => Results.Ok(result.Account),
            AccountUpdateStatus.NotFound => Results.NotFound(),
            AccountUpdateStatus.Conflict =>
                Results.Conflict(new { error = "The account was modified by someone else; reload and retry." }),
            _ => Results.Problem("Unexpected update outcome."),
        };
    }
}
