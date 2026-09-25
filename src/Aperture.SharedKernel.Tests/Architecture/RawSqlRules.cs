namespace Aperture.SharedKernel.Tests.Architecture;

/// <summary>
/// The parsed form of <c>scripts/rawsql-rules.txt</c> — the single definition of the raw-SQL
/// pattern and its exemptions that <c>measure.sh rawsql</c>, GATE 2 and
/// <see cref="RawSqlIsScopedTests"/> all read (011-P7). <see cref="ExemptionFor"/> implements the
/// same three rule kinds as <c>rawsql_exemption</c> in <c>measure.sh</c>, in the same order.
/// </summary>
internal sealed record RawSqlRules(
    string Pattern,
    IReadOnlyList<string> ExemptSegments,
    IReadOnlyList<string> ExemptDirs,
    IReadOnlyList<string> ExemptFiles)
{
    public static RawSqlRules Load(string path) => Parse(File.ReadAllLines(path));

    public static RawSqlRules Parse(IEnumerable<string> lines)
    {
        var entries = lines
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToList();

        List<string> All(string key) => entries.Where(kv => kv[0] == key).Select(kv => kv[1]).ToList();

        var patterns = All("pattern");

        // Fail closed, like measure.sh: a missing or doubled pattern must not silently become a
        // detector that matches nothing.
        if (patterns is not [var pattern] || string.IsNullOrWhiteSpace(pattern))
        {
            throw new InvalidOperationException(
                $"rawsql-rules.txt must define exactly one non-empty pattern= line; found {patterns.Count}.");
        }

        return new RawSqlRules(pattern, All("exempt-segment"), All("exempt-dir"), All("exempt-file"));
    }

    /// <summary>Why a repo-relative path is exempt, or <c>null</c> for a production file.</summary>
    public string? ExemptionFor(string relativePath)
    {
        if (ExemptSegments.Any(s => relativePath.Contains(s, StringComparison.Ordinal)))
        {
            return "test project — exempt by path";
        }

        if (ExemptDirs.Any(d => relativePath.StartsWith(d, StringComparison.Ordinal)))
        {
            return "sanctioned wrapper — allowed";
        }

        return ExemptFiles.Any(f => string.Equals(relativePath, f, StringComparison.Ordinal))
            ? "exempt by exact path"
            : null;
    }
}
