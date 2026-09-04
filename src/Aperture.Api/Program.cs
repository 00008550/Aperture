using System.Diagnostics;
using Aperture.Api.Authentication;
using Aperture.Api.Authorization;
using Aperture.Api.Endpoints;
using Aperture.Modules.Access;
using Aperture.Modules.Sales;
using Aperture.SharedKernel.Data;

var builder = WebApplication.CreateBuilder(args);

// The EF owner connection: the role that owns the schemas, runs migrations, and bypasses RLS.
var ownerConnectionString =
    builder.Configuration.GetConnectionString("Aperture")
    ?? "Host=localhost;Port=5433;Database=aperture;Username=aperture;Password=aperture";

builder.Services.AddAccessModule(ownerConnectionString);

// The tenant-wide discount approval threshold (DOMAIN.md §2 rule 3, open question 2): the percent above
// which a deal must have a lead's approval to be won. A single configurable value for 002; absent config
// falls back to the module default.
var discountApprovalThresholdPct =
    builder.Configuration.GetValue<decimal?>("Sales:DiscountApprovalThresholdPct") ?? 20m;
builder.Services.AddSalesModule(ownerConnectionString, discountApprovalThresholdPct);

// The raw-SQL read path (009), wired here for the first time — 002 is its first consumer. The reader
// connects as the least-privilege `aperture_reader` role that the row-security policies bind to, over
// a connection string DISTINCT from the EF owner above: a different username, and a password that is a
// deploy secret (Aperture:ReaderPassword), never the owner credential and never committed. The
// AddScopeReaderRole migration (009-P3) created the role password-less precisely so the secret is
// provisioned out of band. Building the data source opens no connection, so an absent secret does not
// stop the host booting — the reader simply stays unused until an endpoint queries through it.
var readerConnectionString =
    builder.Configuration.GetConnectionString("ApertureReader")
    ?? "Host=localhost;Port=5433;Database=aperture;Username=aperture_reader";
var readerPassword =
    builder.Configuration["Aperture:ReaderPassword"]
    // Localhost dev default only, mirroring the owner connection's dev fallback above. Production
    // supplies Aperture:ReaderPassword from its secret store; this value matches the test role password.
    ?? "aperture_reader";
builder.Services.AddScopedReader(readerConnectionString, readerPassword);

// Subscribe the host to the scoped-read ActivitySource so its spans are actually created: an
// ActivitySource whose activities no one listens to returns null from StartActivity, and the
// tenant/scope-count telemetry the wrapper emits would never materialise. A full exporter pipeline
// (OpenTelemetry) is 008's concern; this listener is the minimal subscription that makes the spans
// real in the meantime. It samples this one source only — nothing else in the process is affected.
var scopedReadListener = new ActivityListener
{
    ShouldListenTo = source => source.Name == ScopedConnection.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
};
ActivitySource.AddActivityListener(scopedReadListener);
builder.Services.AddSingleton(scopedReadListener);

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
app.MapAccountEndpoints();
app.MapContactEndpoints();
app.MapDealEndpoints();

app.Run();

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
