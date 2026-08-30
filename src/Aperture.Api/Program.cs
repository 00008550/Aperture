using Aperture.Api.Authentication;
using Aperture.Api.Authorization;
using Aperture.Api.Endpoints;
using Aperture.Modules.Access;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAccessModule(
    builder.Configuration.GetConnectionString("Aperture")
    ?? "Host=localhost;Port=5433;Database=aperture;Username=aperture;Password=aperture");

// Bearer tokens carry identity only. What the caller holds is resolved from the access schema
// on every request (001-P3), so a revoked membership stops working immediately rather than
// when the token expires.
builder.Services.AddApertureAuthentication(builder.Configuration);
builder.Services.AddAperturePermissionAuthorization();

// Split liveness from readiness: a readiness probe that reports dependencies is what
// makes a rolling deploy fail safely (ARCHITECTURE.md §10).
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseAuthentication();

// After authentication and before authorization: the tenant comes from the resolved principal,
// never from a header, and it must be established before anything queries.
app.UseMiddleware<TenantScopeMiddleware>();

app.UseAuthorization();

// AllowAnonymous by design: probes run before any principal exists, and they expose
// no tenant data. Every other route carries a policy (CLAUDE.md invariant 4).
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.MapMeEndpoints();

app.Run();

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
