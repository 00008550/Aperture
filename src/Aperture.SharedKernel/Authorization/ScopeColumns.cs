using System.Text.RegularExpressions;

namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// Where the scope columns live in a raw-SQL query: a table alias and the five
/// <see cref="IScopedResource"/> column names (009-P2).
/// <para>
/// The raw-SQL path has no entity type to reflect over — a raw read maps to a DTO shaped like the
/// projection, not like the row — so the caller must name the alias and columns explicitly. That
/// is also what makes a fragment safe to <c>AND</c> into a join: the developer, not a guess,
/// decides which alias each scoped table wears.
/// </para>
/// <para>
/// The alias and every column name are the <em>only</em> caller-supplied text that appears inline
/// in the emitted SQL (every scope value is a bound parameter), so each is validated as a plain
/// identifier at construction. A malformed one is an <see cref="ArgumentException"/> here, never a
/// sanitised-and-continued string — sanitising an identifier is how injection sneaks back in.
/// </para>
/// </summary>
public sealed record ScopeColumns
{
    // A plain SQL identifier: a letter or underscore, then letters, digits or underscores. No
    // quoting, no dots, no whitespace — anything else is rejected rather than escaped.
    private static readonly Regex Identifier =
        new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _tenantId;
    private readonly string _ownerUserId;
    private readonly string _teamId;
    private readonly string _regionId;
    private readonly string _accountId;

    private ScopeColumns(
        string alias,
        string tenantId,
        string ownerUserId,
        string teamId,
        string regionId,
        string accountId)
    {
        Alias = Validate(alias, nameof(alias));
        _tenantId = Validate(tenantId, nameof(tenantId));
        _ownerUserId = Validate(ownerUserId, nameof(ownerUserId));
        _teamId = Validate(teamId, nameof(teamId));
        _regionId = Validate(regionId, nameof(regionId));
        _accountId = Validate(accountId, nameof(accountId));
    }

    /// <summary>The table alias every column is qualified with.</summary>
    public string Alias { get; }

    /// <summary>Alias-qualified <c>tenant_id</c> reference, e.g. <c>o.tenant_id</c>.</summary>
    public string TenantId => $"{Alias}.{_tenantId}";

    public string OwnerUserId => $"{Alias}.{_ownerUserId}";

    public string TeamId => $"{Alias}.{_teamId}";

    public string RegionId => $"{Alias}.{_regionId}";

    public string AccountId => $"{Alias}.{_accountId}";

    /// <summary>
    /// The columns for <paramref name="alias"/> using the repository's snake_case defaults —
    /// <c>tenant_id</c>, <c>owner_user_id</c>, <c>team_id</c>, <c>region_id</c>, <c>account_id</c>.
    /// </summary>
    public static ScopeColumns For(string alias) =>
        new(alias, "tenant_id", "owner_user_id", "team_id", "region_id", "account_id");

    /// <summary>
    /// The columns for <paramref name="alias"/> with explicit column names, for a table whose
    /// columns are not the snake_case defaults.
    /// </summary>
    public static ScopeColumns For(
        string alias,
        string tenantId,
        string ownerUserId,
        string teamId,
        string regionId,
        string accountId) =>
        new(alias, tenantId, ownerUserId, teamId, regionId, accountId);

    private static string Validate(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        return Identifier.IsMatch(value)
            ? value
            : throw new ArgumentException(
                $"'{value}' is not a plain SQL identifier. Aliases and column names are emitted " +
                "inline, so only [A-Za-z_][A-Za-z0-9_]* is accepted — never a quoted, dotted or " +
                "spaced value.",
                paramName);
    }
}
