using System.Linq.Expressions;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.SharedKernel.Authorization;

/// <summary>
/// One grant of row-level access (ARCHITECTURE.md §3). A closed hierarchy — the private
/// constructor means the only cases are the ones declared here, so an exhaustive switch in a
/// future SQL translator (001-P4) stays exhaustive.
/// </summary>
public abstract record DataScope
{
    private DataScope()
    {
    }

    /// <summary>Whether this single grant admits <paramref name="resource"/>.</summary>
    public abstract bool Admits(IScopedResource resource);

    /// <summary>
    /// The same rule as <see cref="Admits"/>, expressed as a boolean expression over
    /// <paramref name="row"/> so a query provider can turn it into a <c>WHERE</c> clause
    /// (001-P4).
    /// <para>
    /// Abstract rather than a <c>switch</c> in the translator on purpose: a new scope kind then
    /// fails to compile until it says how it filters, instead of falling into a default branch
    /// and filtering nothing.
    /// </para>
    /// </summary>
    public abstract Expression ToPredicateBody(ScopeRowExpressions row);

    /// <summary>
    /// The same rule again, this time as a raw-SQL boolean over the columns named by
    /// <paramref name="columns"/>, for the raw-SQL read path (009-P2). Returns the text of this single
    /// grant; <paramref name="parameters"/> collects every value it references so no scope value
    /// is ever inlined as a literal.
    /// <para>
    /// A third abstract member beside <see cref="Admits"/> and <see cref="ToPredicateBody"/>, and
    /// abstract for the same reason: a sixth scope kind must fail to compile until it says how it
    /// filters in SQL, rather than falling into a <c>default:</c> that filters nothing. Internal
    /// because it emits SQL fragments the mutable <see cref="ScopeParameterBag"/> co-owns —
    /// callers compose a whole set through <see cref="ScopeSql.ToSqlFragment"/>, never a grant at
    /// a time.
    /// </para>
    /// </summary>
    internal abstract string ToSqlFragment(ScopeColumns columns, ScopeParameterBag parameters);

    /// <summary>Rows the user owns.</summary>
    public sealed record Self(UserId UserId) : DataScope
    {
        public override bool Admits(IScopedResource resource) =>
            resource.OwnerUserId == UserId;

        public override Expression ToPredicateBody(ScopeRowExpressions row) =>
            Expression.Equal(row.OwnerUserId, ScopeRowExpressions.Parameterised(UserId));

        internal override string ToSqlFragment(ScopeColumns columns, ScopeParameterBag parameters) =>
            $"{columns.OwnerUserId} = {parameters.Add(UserId.Value)}";
    }

    /// <summary>Rows owned by a team. A row with no team is not admitted.</summary>
    public sealed record Team(Guid TeamId) : DataScope
    {
        public override bool Admits(IScopedResource resource) =>
            resource.TeamId is { } team && team == TeamId;

        // Compared as a nullable Guid, so a row with no team yields SQL NULL = @p, which is
        // unknown and therefore not a match. Absent data narrows; it never widens.
        public override Expression ToPredicateBody(ScopeRowExpressions row) =>
            Expression.Equal(row.TeamId, ScopeRowExpressions.Parameterised<Guid?>(TeamId));

        // team_id = @p. A row with no team yields SQL NULL = @p, which is unknown and therefore
        // not a match — absent data narrows, exactly as in the expression form above.
        internal override string ToSqlFragment(ScopeColumns columns, ScopeParameterBag parameters) =>
            $"{columns.TeamId} = {parameters.Add(TeamId)}";
    }

    /// <summary>Rows in a region. A row with no region is not admitted.</summary>
    public sealed record Region(Guid RegionId) : DataScope
    {
        public override bool Admits(IScopedResource resource) =>
            resource.RegionId is { } region && region == RegionId;

        // Compared as a nullable Guid, so a row with no region yields SQL NULL = @p, which is
        // unknown and therefore not a match. Absent data narrows; it never widens.
        public override Expression ToPredicateBody(ScopeRowExpressions row) =>
            Expression.Equal(row.RegionId, ScopeRowExpressions.Parameterised<Guid?>(RegionId));

        // region_id = @p. NULL region yields NULL = @p (unknown, not a match): absent data narrows.
        internal override string ToSqlFragment(ScopeColumns columns, ScopeParameterBag parameters) =>
            $"{columns.RegionId} = {parameters.Add(RegionId)}";
    }

    /// <summary>One named account — for a key-account handler.</summary>
    public sealed record Account(Guid AccountId) : DataScope
    {
        public override bool Admits(IScopedResource resource) =>
            resource.AccountId is { } account && account == AccountId;

        // Compared as a nullable Guid, so a row with no account yields SQL NULL = @p, which is
        // unknown and therefore not a match. Absent data narrows; it never widens.
        public override Expression ToPredicateBody(ScopeRowExpressions row) =>
            Expression.Equal(row.AccountId, ScopeRowExpressions.Parameterised<Guid?>(AccountId));

        // account_id = @p. NULL account yields NULL = @p (unknown, not a match): absent data narrows.
        internal override string ToSqlFragment(ScopeColumns columns, ScopeParameterBag parameters) =>
            $"{columns.AccountId} = {parameters.Add(AccountId)}";
    }

    /// <summary>
    /// Everything inside the tenant. Explicit and auditable, never implied by an absent filter —
    /// the difference between this and "no scopes" is the whole point of the design.
    /// The tenant boundary itself is enforced by <see cref="DataScopeSet"/>, not here.
    /// </summary>
    public sealed record AllTenant : DataScope
    {
        public override bool Admits(IScopedResource resource) => true;

        // True inside the tenant. The tenant equality itself is added by the translator and
        // cannot be reached past, so this is never "every row in the database".
        public override Expression ToPredicateBody(ScopeRowExpressions row) =>
            Expression.Constant(true);

        // TRUE inside the tenant. The tenant equality is conjoined outside the grant union by
        // ScopeSql and cannot be reached past, so this is never "every row in the database". No
        // parameter, because there is no value to bind.
        internal override string ToSqlFragment(ScopeColumns columns, ScopeParameterBag parameters) =>
            "TRUE";
    }
}
