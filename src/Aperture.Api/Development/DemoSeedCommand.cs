namespace Aperture.Api.Development;

/// <summary>What the host should do with its command line, decided before anything touches a database.</summary>
public enum DemoSeedDecision
{
    /// <summary>No <c>--seed-demo</c>: serve as usual.</summary>
    Serve = 1,

    /// <summary><c>--seed-demo</c> on a Development host: seed, then exit.</summary>
    Seed = 2,

    /// <summary><c>--seed-demo</c> anywhere else: log, exit non-zero, touch nothing.</summary>
    Refuse = 3,
}

/// <summary>
/// The <c>--seed-demo</c> host flag (010-P5a). A flag on the API host rather than a separate tool, because
/// the host already composes both modules' contexts, connection strings and reader config — a second
/// composition root would drift from this one.
/// </summary>
public static class DemoSeedCommand
{
    public const string Flag = "--seed-demo";

    /// <summary>Exit code when the flag is refused outside Development.</summary>
    public const int RefusedExitCode = 2;

    /// <summary>Exit code when the seed itself failed. A partial seed is completed by the next run.</summary>
    public const int FailedExitCode = 1;

    /// <summary>The arguments the host builder should see: the flag removed, so the configuration
    /// command-line provider never has to interpret a value-less switch.</summary>
    public static string[] HostArgs(string[] args) =>
        [.. args.Where(a => !string.Equals(a, Flag, StringComparison.Ordinal))];

    /// <summary>
    /// Decides from the arguments and the environment alone — no services, no connection — so a refusal
    /// provably happens before anything could reach a database. Fail closed: only an environment that is
    /// exactly Development may seed; Production, Staging, Testing and anything unrecognised refuse.
    /// </summary>
    public static DemoSeedDecision Decide(string[] args, IHostEnvironment environment, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        if (!args.Contains(Flag, StringComparer.Ordinal))
        {
            return DemoSeedDecision.Serve;
        }

        if (!environment.IsDevelopment())
        {
            logger.LogError(
                "{Flag} is honoured only in Development; this host is running as {Environment}. Nothing was changed.",
                Flag,
                environment.EnvironmentName);
            return DemoSeedDecision.Refuse;
        }

        return DemoSeedDecision.Seed;
    }

    /// <summary>Runs the seed in its own scope and returns the process exit code.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DemoSeed>().RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The demo seed failed. Re-running it completes whatever is missing.");
            return FailedExitCode;
        }
    }
}
