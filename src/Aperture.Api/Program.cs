using Aperture.Modules.Access;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAccessModule(
    builder.Configuration.GetConnectionString("Aperture")
    ?? "Host=localhost;Port=5433;Database=aperture;Username=aperture;Password=aperture");

// Split liveness from readiness: a readiness probe that reports dependencies is what
// makes a rolling deploy fail safely (ARCHITECTURE.md §10).
builder.Services.AddHealthChecks();

var app = builder.Build();

// AllowAnonymous by design: probes run before any principal exists, and they expose
// no tenant data. Every other route carries a policy (CLAUDE.md invariant 4).
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.Run();

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
