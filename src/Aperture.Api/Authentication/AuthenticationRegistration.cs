using System.Security.Claims;
using System.Text;
using Aperture.Modules.Access.Authentication;
using Aperture.SharedKernel.Multitenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Aperture.Api.Authentication;

/// <summary>
/// The JWT bearer scheme, plus the step that turns a validated token into a resolved
/// <see cref="AccessPrincipal"/>.
/// </summary>
public static class AuthenticationRegistration
{
    /// <summary>Where the resolved principal lives for the rest of the request.</summary>
    private const string PrincipalItemKey = "aperture.access-principal";

    public static IServiceCollection AddApertureAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new ApertureJwtOptions();
        configuration.GetSection(ApertureJwtOptions.SectionName).Bind(options);
        options.Validate();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                // Keep the raw claim names. With the inbound mapper on, "sub" arrives as a
                // WS-Federation URI and every lookup here silently misses.
                jwt.MapInboundClaims = false;

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                    ValidateLifetime = true,
                    // Pinned so a token cannot nominate its own algorithm. "alg": "none" and
                    // RS256/HS256 confusion are both closed by naming the one we accept.
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AccessClaimTypes.Subject,
                };

                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ResolvePrincipalAsync,
                };
            });

        return services;
    }

    /// <summary>
    /// A structurally valid token is not yet an authenticated caller. This turns the token's
    /// <c>sub</c> and <c>tenant_id</c> into a real membership, and fails the authentication
    /// when it cannot — so a token naming a tenant the user does not belong to comes back 401,
    /// not 403 against somebody else's data.
    /// </summary>
    private static async Task ResolvePrincipalAsync(TokenValidatedContext context)
    {
        var claims = context.Principal;

        if (claims is null
            || !Guid.TryParse(claims.FindFirstValue(AccessClaimTypes.Subject), out var userGuid)
            || !Guid.TryParse(claims.FindFirstValue(AccessClaimTypes.TenantId), out var tenantGuid))
        {
            context.Fail("The token does not carry a well-formed subject and tenant.");
            return;
        }

        var resolver = context.HttpContext.RequestServices.GetRequiredService<IAccessPrincipalResolver>();

        var principal = await resolver.ResolveAsync(
            new TenantId(tenantGuid),
            new UserId(userGuid),
            context.HttpContext.RequestAborted);

        if (principal is null)
        {
            context.Fail("The token's subject has no active membership in the tenant it names.");
            return;
        }

        context.HttpContext.Items[PrincipalItemKey] = principal;

        // Permissions become claims once, here, so authorization is a claim check rather than
        // a database round trip per requirement.
        var identity = new ClaimsIdentity();
        foreach (var permission in principal.Permissions.Values)
        {
            identity.AddClaim(new Claim(AccessClaimTypes.Permission, permission));
        }

        claims.AddIdentity(identity);
    }

    /// <summary>
    /// The resolved principal for this request.
    /// </summary>
    /// <exception cref="InvalidOperationException">The request was not authenticated. Reaching
    /// this from an anonymous endpoint is a bug in the endpoint, so it throws rather than
    /// handing back a null a caller might treat as "no restrictions".</exception>
    public static AccessPrincipal GetAccessPrincipal(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items[PrincipalItemKey] as AccessPrincipal
            ?? throw new InvalidOperationException(
                "No access principal is attached to this request. The endpoint must require authentication.");
    }

    /// <summary>The resolved principal, or null when the request is anonymous.</summary>
    public static AccessPrincipal? FindAccessPrincipal(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[PrincipalItemKey] as AccessPrincipal;
    }
}
