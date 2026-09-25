using System.Text.RegularExpressions;

namespace Aperture.SharedKernel.Tests.Architecture;

/// <summary>
/// CLAUDE.md invariant 2 — raw SQL bypasses EF's global tenant filter, so it may reach the
/// database only through the one sanctioned wrapper project (<c>Aperture.SharedKernel/Data</c>,
/// landed by 009-P3). This asserts that rule against the source tree: any file under
/// <c>src/</c> outside a test project and outside the sanctioned project that names a raw-SQL
/// entry point fails the build and is reported with its path and line.
///
/// <para><c>scripts/measure.sh rawsql</c> greps for the same rule; this is the version that
/// fails CI, mirroring the endpoint-policy gate's grep/test pairing.</para>
///
/// <para>Deliberately first (009-P1): the constraint exists before the Dapper reference it
/// constrains (009-P3), so the package cannot land into a repository with nothing watching it.</para>
/// </summary>
public sealed class RawSqlIsScopedTests
{
    /// <summary>
    /// The pattern and exemptions are read from <c>scripts/rawsql-rules.txt</c> — the same file
    /// <c>scripts/measure.sh rawsql</c> and GATE 2 read (011-P7). Never restate them here: three
    /// private copies of this pattern drifted apart once already (011-F5), and an
    /// <c>ExecuteSqlAsync</c> in production passed this test while GATE 2 saw it.
    /// </summary>
    private static readonly RawSqlRules Rules = RawSqlRules.Load(
        Path.Combine(RepoRoot(), "scripts", "rawsql-rules.txt"));

    /// <summary>
    /// The entry points that escape EF Core's global query filter (see the rules file for the
    /// alternation and why a bare <c>.Query&lt;</c> is not in it). A comment mentioning one still
    /// matches — a mention is a use we want to see.
    /// </summary>
    private static readonly Regex RawSqlEntryPoint = new(Rules.Pattern, RegexOptions.Compiled);

    /// <summary>Dapper as a package dependency, however the version is (or is not) pinned.</summary>
    private static readonly Regex DapperPackageReference = new(
        "<PackageReference\\s+Include=\"Dapper\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A production <c>.cs</c> file is one under <c>src/</c> that no rule in the rules file exempts
    /// (test-project segment, sanctioned wrapper directory, or an exact file). The exemption is a
    /// path rule, not a magic comment: a comment a developer can paste anywhere is a bypass, not an
    /// exemption (009 edge case 14).
    /// </summary>
    private static bool IsExemptCSharp(string relativePath) => Rules.ExemptionFor(relativePath) is not null;

    /// <summary>
    /// The one project allowed to reference the Dapper package (009-P3 puts it there). Test
    /// projects are also exempt — they exercise the wrapper. Everything else referencing Dapper is
    /// a second door into raw SQL (edge case 13).
    /// </summary>
    private static bool IsExemptProject(string relativePath) =>
        relativePath.Contains(".Tests/", StringComparison.Ordinal)
        || relativePath.EndsWith("Aperture.SharedKernel/Aperture.SharedKernel.csproj", StringComparison.Ordinal);

    [Fact]
    public void No_production_source_file_reaches_raw_SQL_outside_the_sanctioned_project()
    {
        var offenders = Offenders(LiveSourceTree());

        Assert.True(
            offenders.Count == 0,
            "Raw-SQL entry point outside the sanctioned wrapper project — raw SQL bypasses the "
            + "tenant query filter and must go through Aperture.SharedKernel/Data (009):\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_project_outside_the_sanctioned_one_references_the_Dapper_package()
    {
        var offenders = EnumerateSource("*.csproj")
            .Select(RelativeToRepo)
            .Where(relative => !IsExemptProject(relative))
            .Where(relative => DapperPackageReference.IsMatch(File.ReadAllText(RepoAbsolute(relative))))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A project other than the sanctioned wrapper references Dapper — that is a second, "
            + "ungated path to raw SQL:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Exactly_one_project_references_the_Dapper_package()
    {
        // 009-P4 lands the one Dapper reference into the sanctioned project. "Exactly one" is
        // stronger than "no others": it also catches the reference silently vanishing (a merge
        // that drops it, and with it the only door to raw SQL) — the wrapper would then not
        // compile, but this pins the invariant at the project graph regardless.
        var referencing = EnumerateSource("*.csproj")
            .Select(RelativeToRepo)
            .Where(relative => DapperPackageReference.IsMatch(File.ReadAllText(RepoAbsolute(relative))))
            .ToList();

        Assert.True(
            referencing is [_],
            "Exactly one project must reference Dapper (the sanctioned wrapper). Found:\n  "
            + string.Join("\n  ", referencing));
        Assert.EndsWith(
            "Aperture.SharedKernel/Aperture.SharedKernel.csproj",
            referencing[0],
            StringComparison.Ordinal);
    }

    // --- Detector self-verification -------------------------------------------------------------
    // A scanner that finds nothing because its regex is broken passes identically to one that
    // finds nothing because the code is clean, and that failure is invisible. So the detector is
    // exercised against fixture strings, not only against the live tree (009-P1 plan requirement).

    [Theory]
    [InlineData("using var conn = new NpgsqlConnection(cs);")]
    [InlineData("var rows = conn.Query<Row>(sql);           // Dapper")]
    [InlineData("_ctx.Widgets.FromSqlRaw(\"select * from w\");")]
    [InlineData("_ctx.Widgets.FromSqlInterpolated($\"select {x}\");")]
    [InlineData("_ctx.Database.ExecuteSqlRaw(\"delete from w\");")]
    // 011-P7: the forms only GATE 2 used to see (011-F5).
    [InlineData("await db.Database.ExecuteSqlAsync($\"delete from w where id = {id}\");")]
    [InlineData("db.Database.ExecuteSql($\"delete from w\");")]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"delete from w\");")]
    [InlineData("db.Database.ExecuteSqlInterpolated($\"delete from w\");")]
    [InlineData("await db.Database.ExecuteSqlInterpolatedAsync($\"delete from w\");")]
    [InlineData("var w = _ctx.Widgets.FromSql($\"select * from w\");")]
    [InlineData("var n = db.Database.SqlQuery<int>($\"select 1\");")]
    [InlineData("var n = db.Database.SqlQueryRaw<int>(\"select 1\");")]
    // A Dapper `.Query<` cannot compile without the `using` that names it.
    [InlineData("using Dapper;")]
    [InlineData("var rows = Dapper.SqlMapper.Query<Row>(conn, sql);")]
    public void The_detector_flags_a_known_raw_SQL_line(string line) =>
        Assert.Matches(RawSqlEntryPoint, line);

    [Theory]
    [InlineData("var rows = _ctx.Widgets.Where(w => w.TenantId == t).ToList();")]
    [InlineData("// nothing raw about this line at all")]
    [InlineData("public sealed record ScopeFragment(string Sql);")]
    // The sanctioned door itself: a call on ScopedConnection is the invariant being honoured.
    [InlineData("var rows = await _reader.QueryAsync<DealGridRow>(sql, scopes, ct);")]
    [InlineData("var b = new NpgsqlConnectionStringBuilder(cs);")]
    [InlineData("await host.ExecuteAsync(ct);")]
    public void The_detector_leaves_clean_lines_alone(string line) =>
        Assert.DoesNotMatch(RawSqlEntryPoint, line);

    // --- 011 edge 22: detector parity ----------------------------------------------------------

    [Fact]
    public void A_planted_ExecuteSqlAsync_in_a_production_file_is_an_offender()
    {
        var offenders = Offenders(
        [
            ("src/Modules/Sales/Aperture.Modules.Sales/Application/Planted.cs",
                ["class P {", "  Task M() => db.Database.ExecuteSqlAsync($\"delete from sales.deals\");", "}"]),
        ]);

        Assert.Equal(new[] { "src/Modules/Sales/Aperture.Modules.Sales/Application/Planted.cs:2" }, offenders);
    }

    [Fact]
    public void DemoSeed_is_exempt_by_exact_path_and_no_other_Development_file_is()
    {
        const string line = "await access.Database.ExecuteSqlAsync($\"ALTER ROLE x\");";

        var offenders = Offenders(
        [
            ("src/Aperture.Api/Development/DemoSeed.cs", [line]),
            ("src/Aperture.Api/Development/OtherSeed.cs", [line]),
            ("src/Aperture.Api/Development/DemoSeed.cs.orig", [line]),
            ("src/Modules/Sales/Aperture.Modules.Sales/Development/DemoSeed.cs", [line]),
        ]);

        Assert.Equal(
            new[]
            {
                "src/Aperture.Api/Development/OtherSeed.cs:1",
                "src/Aperture.Api/Development/DemoSeed.cs.orig:1",
                "src/Modules/Sales/Aperture.Modules.Sales/Development/DemoSeed.cs:1",
            },
            offenders);
    }

    [Fact]
    public void The_rules_file_defines_the_exemptions_the_three_detectors_share()
    {
        Assert.Equal(new[] { ".Tests/" }, Rules.ExemptSegments);
        Assert.Equal(new[] { "src/Aperture.SharedKernel/Data/" }, Rules.ExemptDirs);
        Assert.Equal(new[] { "src/Aperture.Api/Development/DemoSeed.cs" }, Rules.ExemptFiles);
    }

    [Theory]
    [InlineData("# only a comment")]
    [InlineData("pattern=")]
    [InlineData("pattern=a\npattern=b")]
    public void A_rules_file_without_exactly_one_pattern_fails_closed(string content) =>
        Assert.Throws<InvalidOperationException>(() => RawSqlRules.Parse(content.Split('\n')));

    [Fact]
    public void Measure_sh_reads_the_rules_file_and_carries_no_pattern_of_its_own()
    {
        var script = File.ReadAllText(RepoAbsolute("scripts/measure.sh"));

        Assert.Contains("scripts/rawsql-rules.txt", script, StringComparison.Ordinal);
        // The pre-011-P7 private copies. If one of these alternations reappears in the script, a
        // detector has grown its own definition again.
        Assert.DoesNotContain("FromSqlRaw|", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteSql(Raw", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DemoSeed", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Measure_sh_rawsql_and_this_test_classify_every_match_in_src_identically()
    {
        // The two engines (GNU grep -E and .NET Regex) read the same pattern string; this proves
        // they also agree on what it matches, line for line, and on which matches are exempt.
        var expected = LiveSourceTree()
            .SelectMany(f => f.Lines
                .Select((text, i) => (text, i))
                .Where(x => RawSqlEntryPoint.IsMatch(x.text))
                .Select(x => $"{f.Relative}:{x.i + 1} {(Rules.ExemptionFor(f.Relative) is null ? "PRODUCTION" : "exempt")}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        var output = RunMeasure("rawsql");
        var actual = output
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Contains(".cs:", StringComparison.Ordinal))
            .Select(l =>
            {
                var location = l[(l.LastIndexOf(' ') + 1)..];
                return $"{location} {(l.Contains("PRODUCTION CALL SITE", StringComparison.Ordinal) ? "PRODUCTION" : "exempt")}";
            })
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
    }

    /// <summary>Every non-exempt line in <paramref name="files"/> that names a raw-SQL entry point.</summary>
    private static List<string> Offenders(IEnumerable<(string Relative, string[] Lines)> files) =>
        files
            .Where(f => !IsExemptCSharp(f.Relative))
            .SelectMany(f => f.Lines
                .Select((text, i) => (text, i))
                .Where(x => RawSqlEntryPoint.IsMatch(x.text))
                .Select(x => $"{f.Relative}:{x.i + 1}"))
            .ToList();

    private static IEnumerable<(string Relative, string[] Lines)> LiveSourceTree() =>
        EnumerateSource("*.cs").Select(file => (RelativeToRepo(file), File.ReadAllLines(file)));

    private static string RunMeasure(string mode)
    {
        // On Windows a bare `bash` can resolve to WSL's System32\bash.exe, which sees a different
        // filesystem; prefer Git Bash when it is installed. CI (ubuntu) uses PATH.
        var gitBash = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        var bash = OperatingSystem.IsWindows() && File.Exists(gitBash) ? gitBash : "bash";

        var psi = new System.Diagnostics.ProcessStartInfo(bash)
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("scripts/measure.sh");
        psi.ArgumentList.Add(mode);

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "measure.sh did not finish");
        Assert.True(stderr.Result.Length == 0, "measure.sh wrote to stderr:\n" + stderr.Result);
        return stdout.Result;
    }

    [Fact]
    public void The_detector_flags_a_Dapper_package_reference_fixture() =>
        Assert.Matches(DapperPackageReference, "<PackageReference Include=\"Dapper\" />");

    [Fact]
    public void A_test_project_path_is_exempt_by_rule_even_when_it_names_raw_SQL()
    {
        // AccessSchemaTests.cs legitimately uses NpgsqlConnection today; the rule must not flag it
        // (edge case 14). The exemption is the path, independent of the file's content.
        const string testFile = "src/Modules/Access/Aperture.Modules.Access.Tests/AccessSchemaTests.cs";
        Assert.Matches(RawSqlEntryPoint, "using var conn = new NpgsqlConnection(cs);");
        Assert.True(IsExemptCSharp(testFile));
    }

    // --- Repo traversal -------------------------------------------------------------------------

    private static IEnumerable<string> EnumerateSource(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), pattern, SearchOption.AllDirectories)
            .Where(p => !p.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal)
                        && !p.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal));

    private static string RelativeToRepo(string absolute) =>
        Path.GetRelativePath(RepoRoot(), absolute).Replace('\\', '/');

    private static string RepoAbsolute(string relative) =>
        Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

    private static string? _repoRoot;

    private static string RepoRoot()
    {
        if (_repoRoot is not null)
        {
            return _repoRoot;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Aperture.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Aperture.slnx) above the test binary.");
        return _repoRoot = dir!.FullName;
    }
}
