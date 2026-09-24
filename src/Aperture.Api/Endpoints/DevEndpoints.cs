using System.Text;
using Aperture.Api.Authentication;
using Aperture.Api.Development;
using Aperture.Modules.Access.Authentication;
using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Aperture.Api.Endpoints;

/// <summary>Who to mint a Development token for. Exactly one of each pair is read; ids win over names.</summary>
public sealed record DevTokenRequest(string? TenantSlug, Guid? TenantId, string? UserEmail, Guid? UserId);

public sealed record DevTokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>One demo user, as the console's Development sign-in picker lists it.</summary>
public sealed record DevUserResponse(Guid UserId, string Email, string DisplayName, string Demonstrates);

public sealed record DevUsersResponse(Guid TenantId, string TenantSlug, string TenantName, IReadOnlyList<DevUserResponse> Users);

/// <summary>
/// Development-only sign-in affordances (010-P5a): list the seeded demo users, and mint a bearer token for
/// one of them. Mapped by <c>Program.cs</c> only inside <c>IsDevelopment()</c>, so in every other
/// environment these routes do not exist — stronger than an authorization check a misconfiguration could
/// open. Each handler re-checks the environment as well.
/// </summary>
public static class DevEndpoints
{
    /// <summary>Short-lived on purpose: a dev token outliving a working day is a token nobody meant to keep.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(8);

    public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // AllowAnonymous by necessity (CLAUDE.md invariant 4): these routes exist to obtain the FIRST
        // token, so no principal can exist yet. They are mapped only in Development (Program.cs) and
        // re-check it per request, and they grant nothing beyond an existing active membership — the
        // token carries only sub + tenant_id, and permissions and scopes still resolve from the access
        // schema on every request, exactly as for any other bearer token.
        app.MapGet("/api/dev/users", ListDemoUsers).AllowAnonymous().WithName("DevListUsers");
        app.MapPost("/api/dev/token", MintToken).AllowAnonymous().WithName("DevMintToken");

        return app;
    }

    private static async Task<IResult> ListDemoUsers(
        IHostEnvironment environment,
        AccessDbContext access,
        CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return Results.NotFound();
        }

        // Only the seeded demo tenant, never every tenant in the database.
        var tenant = await access.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.Slug == DemoSeed.TenantSlug && t.IsActive, cancellationToken);
        if (tenant is null)
        {
            return Results.NotFound();
        }

        using (AmbientTenantContext.Begin(tenant.Id))
        {
            var members = await access.Memberships
                .AsNoTracking()
                .Where(m => m.IsActive)
                .Join(access.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, u) => u)
                .ToListAsync(cancellationToken);

            var order = DemoSeed.Personas.Select(p => p.Email).ToList();
            var users = members
                .Select(u => new DevUserResponse(
                    u.Id.Value,
                    u.Email,
                    u.DisplayName,
                    DemoSeed.Personas.FirstOrDefault(p => p.Email == u.Email)?.Demonstrates ?? "Demo user"))
                .OrderBy(u => order.IndexOf(u.Email) is var i and >= 0 ? i : int.MaxValue)
                .ThenBy(u => u.Email, StringComparer.Ordinal)
                .ToList();

            return Results.Ok(new DevUsersResponse(tenant.Id.Value, tenant.Slug, tenant.Name, users));
        }
    }

    private static async Task<IResult> MintToken(
        DevTokenRequest? request,
        IHostEnvironment environment,
        AccessDbContext access,
        IAccessPrincipalResolver resolver,
        ApertureJwtOptions jwt,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger(AuthenticationLog.Category);

        if (!environment.IsDevelopment() || request is null)
        {
            return Results.NotFound();
        }

        var tenantId = await ResolveTenantAsync(access, request, cancellationToken);
        var userId = await ResolveUserAsync(access, request, cancellationToken);

        // The same resolution the bearer handler runs on every request: active tenant, active user, active
        // membership. Anything short of a granted principal is the same uniform 404 — the caller learns
        // nothing about which half failed.
        var resolution = tenantId is { } t && userId is { } u
            ? await resolver.ResolveAsync(t, u, cancellationToken)
            : null;

        if (resolution is not { IsGranted: true, Principal: { } principal })
        {
            AuthenticationLog.DevTokenRefused(logger);
            return Results.NotFound();
        }

        var expires = clock.GetUtcNow().Add(TokenLifetime);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                SecurityAlgorithms.HmacSha256),
            // Identity only. The tenant comes from the verified membership, not the request; no perm or
            // scope claim is ever minted — they would duplicate, and could contradict, per-request resolution.
            Claims = new Dictionary<string, object>
            {
                [AccessClaimTypes.Subject] = principal.UserId.Value.ToString(),
                [AccessClaimTypes.TenantId] = principal.TenantId.Value.ToString(),
            },
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        AuthenticationLog.DevTokenMinted(logger, principal.UserId.Value, principal.TenantId.Value);

        return Results.Ok(new DevTokenResponse(token, expires));
    }

    private static async Task<TenantId?> ResolveTenantAsync(
        AccessDbContext access, DevTokenRequest request, CancellationToken cancellationToken)
    {
        if (request.TenantId is { } id)
        {
            return new TenantId(id);
        }

        if (string.IsNullOrWhiteSpace(request.TenantSlug))
        {
            return null;
        }

        var slug = request.TenantSlug.Trim();
        var tenant = await access.Tenants
            .AsNoTracking()
            .Where(t => t.Slug == slug)
            .Select(t => (TenantId?)t.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return tenant;
    }

    private static async Task<UserId?> ResolveUserAsync(
        AccessDbContext access, DevTokenRequest request, CancellationToken cancellationToken)
    {
        if (request.UserId is { } id)
        {
            return new UserId(id);
        }

        if (string.IsNullOrWhiteSpace(request.UserEmail))
        {
            return null;
        }

        // Emails are stored lower-cased (User.Email is the login).
        var email = request.UserEmail.Trim().ToLowerInvariant();
        var user = await access.Users
            .AsNoTracking()
            .Where(u => u.Email == email)
            .Select(u => (UserId?)u.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return user;
    }
}
