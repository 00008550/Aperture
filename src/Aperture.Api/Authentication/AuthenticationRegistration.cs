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
                    OnAuthenticationFailed = LogTokenRejection,
                };
            });

        return services;
    }

    /// <summary>
    /// A structurally valid token is not yet an authenticated caller. This turns the token's
    /// <c>sub</c> and <c>tenant_id</c> into a real membership, and fails the authentication
    /// when it cannot — so a token naming a tenant the user does not belong to comes back 401,
    /// not 403 against somebody else's data.
    /// <para>
    /// It also <b>replaces</b> the principal rather than adding to it. See
    /// <see cref="BuildPrincipal"/>.
    /// </para>
    /// </summary>
    private static async Task ResolvePrincipalAsync(TokenValidatedContext context)
    {
        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(AuthenticationLog.Category);

        var claims = context.Principal;

        if (claims is null
            || !Guid.TryParse(claims.FindFirstValue(AccessClaimTypes.Subject), out var userGuid)
            || !Guid.TryParse(claims.FindFirstValue(AccessClaimTypes.TenantId), out var tenantGuid))
        {
            AuthenticationLog.MalformedToken(logger);
            context.Fail("The token does not carry a well-formed subject and tenant.");
            return;
        }

        var resolver = context.HttpContext.RequestServices.GetRequiredService<IAccessPrincipalResolver>();

        var resolution = await resolver.ResolveAsync(
            new TenantId(tenantGuid),
            new UserId(userGuid),
            context.HttpContext.RequestAborted);

        if (!resolution.IsGranted || resolution.Principal is not { } principal)
        {
            AuthenticationLog.PrincipalNotResolved(
                logger,
                userGuid,
                tenantGuid,
                // Unreachable in practice — a denial always carries a reason — but a null
                // reason must not silently log as success-shaped noise.
                resolution.Reason ?? AccessDenialReason.NoActiveMembership);

            context.Fail("The token's subject has no active membership in the tenant it names.");
            return;
        }

        context.HttpContext.Items[PrincipalItemKey] = principal;
        context.Principal = BuildPrincipal(principal, context.Scheme.Name);
    }

    /// <summary>
    /// Builds the request's principal from the <em>resolved</em> access principal and nothing
    /// else, discarding the identity the token arrived with.
    /// <para>
    /// This is a whitelist, not a filter, and the difference is the whole point.
    /// <see cref="ClaimsPrincipal.HasClaim(string, string)"/> searches every identity on the
    /// principal, so appending a resolved identity to the token's own left a token-supplied
    /// <c>perm</c> claim indistinguishable, at the moment of the authorization decision, from a
    /// permission read out of <c>access.role_permissions</c>. A well-signed token could name its
    /// own permissions. Stripping <c>perm</c> instead would fix that one claim and leave the
    /// next one someone decides to trust; constructing the principal from scratch means a claim
    /// only exists here because this method put it here.
    /// </para>
    /// </summary>
    private static ClaimsPrincipal BuildPrincipal(AccessPrincipal principal, string authenticationScheme)
    {
        var identity = new ClaimsIdentity(
            authenticationType: authenticationScheme,
            nameType: AccessClaimTypes.Subject,
            roleType: null);

        identity.AddClaim(new Claim(AccessClaimTypes.Subject, principal.UserId.Value.ToString()));
        identity.AddClaim(new Claim(AccessClaimTypes.TenantId, principal.TenantId.Value.ToString()));

        foreach (var permission in principal.Permissions.Values)
        {
            identity.AddClaim(new Claim(AccessClaimTypes.Permission, permission));
        }

        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// JwtBearer logs a validation failure at Information and drops the <c>Fail</c> reason
    /// entirely, so without this the ordinary token failures are the one deny path with no
    /// warning-level signal. The exception type only — its message can carry token contents.
    /// </summary>
    private static Task LogTokenRejection(AuthenticationFailedContext context)
    {
        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(AuthenticationLog.Category);

        AuthenticationLog.TokenRejected(logger, context.Exception.GetType().Name);
        return Task.CompletedTask;
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
