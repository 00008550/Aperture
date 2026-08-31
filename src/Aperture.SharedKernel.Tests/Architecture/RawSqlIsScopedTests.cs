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
    /// The entry points that escape EF Core's global query filter: Dapper, EF's own raw-SQL
    /// escape hatches, and a bare Npgsql connection. Word-bounded so <c>DapperExtensions</c> or a
    /// comment mentioning <c>FromSqlRaw</c> both still match — a mention is a use we want to see.
    /// </summary>
    private static readonly Regex RawSqlEntryPoint = new(
        @"\b(Dapper|NpgsqlConnection|FromSqlRaw|FromSqlInterpolated|ExecuteSqlRaw)\b",
        RegexOptions.Compiled);

    /// <summary>Dapper as a package dependency, however the version is (or is not) pinned.</summary>
    private static readonly Regex DapperPackageReference = new(
        "<PackageReference\\s+Include=\"Dapper\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A production <c>.cs</c> file is one under <c>src/</c> that is not part of a test project and
    /// not in the sanctioned wrapper directory. The exemption is a path rule, not a magic comment:
    /// a comment a developer can paste anywhere is a bypass, not an exemption (edge case 14).
    /// </summary>
    private static bool IsExemptCSharp(string relativePath) =>
        relativePath.Contains(".Tests/", StringComparison.Ordinal)
        || relativePath.Contains("Aperture.SharedKernel/Data/", StringComparison.Ordinal);

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
        var offenders = new List<string>();

        foreach (var file in EnumerateSource("*.cs"))
        {
            var relative = RelativeToRepo(file);
            if (IsExemptCSharp(relative))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (RawSqlEntryPoint.IsMatch(lines[i]))
                {
                    offenders.Add($"{relative}:{i + 1}");
                }
            }
        }

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

    /// <summary>
    /// Tightened by 009-P3: not merely "no one else references Dapper" but "<b>exactly one</b>
    /// project does, and it is the sanctioned wrapper". The negation cannot distinguish a repo where
    /// Dapper is correctly confined from one where it was never added at all — once the package
    /// lands (P3), the single-door guarantee is only real if precisely one door exists.
    /// </summary>
    [Fact]
    public void Exactly_one_project_references_the_Dapper_package_and_it_is_the_sanctioned_one()
    {
        var referencing = EnumerateSource("*.csproj")
            .Select(RelativeToRepo)
            .Where(relative => DapperPackageReference.IsMatch(File.ReadAllText(RepoAbsolute(relative))))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            referencing is ["src/Aperture.SharedKernel/Aperture.SharedKernel.csproj"],
            "Dapper must be referenced by exactly the sanctioned wrapper project and nothing else; "
            + "found: [" + string.Join(", ", referencing) + "]");
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
    public void The_detector_flags_a_known_raw_SQL_line(string line) =>
        Assert.Matches(RawSqlEntryPoint, line);

    [Theory]
    [InlineData("var rows = _ctx.Widgets.Where(w => w.TenantId == t).ToList();")]
    [InlineData("// nothing raw about this line at all")]
    [InlineData("public sealed record ScopeFragment(string Sql);")]
    public void The_detector_leaves_clean_lines_alone(string line) =>
        Assert.DoesNotMatch(RawSqlEntryPoint, line);

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
