namespace Aperture.Modules.Sales.Domain;

/// <summary>
/// The one place the deal lifecycle is written down (DOMAIN.md §2, ARCHITECTURE.md §5): a table of the
/// legal <c>(from, to)</c> edges, each with the guard that must hold before that edge may be taken. Any
/// pair not in the table is illegal — which is also how the terminal states enforce themselves, since
/// <c>won</c> and <c>lost</c> appear only as targets and never as sources, so no edge leaves them. There
/// is no per-stage <c>if</c> ladder anywhere else; a transition that is not in this table is a domain
/// error, not a forgotten branch.
/// <para>
/// The machine only <em>decides</em>: <see cref="Evaluate"/> returns whether an edge is legal and, if so,
/// whether its guard passes. Applying the effect of a legal, guarded transition (advancing the stage,
/// freezing the price-list version, recording the lost reason) belongs to <see cref="Deal.Transition"/>,
/// which owns the aggregate's state — the machine never mutates a deal.
/// </para>
/// </summary>
public static class DealStateMachine
{
    // The legal edges and their guards. The linear pipeline of DOMAIN.md §2
    // (new → qualified → quoted → negotiation → won | lost); every other pair is absent and therefore
    // illegal. won/lost are targets only, so they are terminal by construction — no key has them as a source.
    private static readonly IReadOnlyDictionary<(string From, string To), Func<Deal, DealTransitionInput, DealTransitionStatus?>> Edges =
        new Dictionary<(string, string), Func<Deal, DealTransitionInput, DealTransitionStatus?>>
        {
            [(Deal.Stages.New, Deal.Stages.Qualified)] = static (_, _) => null,

            // Rule 2: moving to quoted freezes the price-list version used, so a later price change cannot
            // alter an outstanding quote. The version must be supplied to be frozen.
            [(Deal.Stages.Qualified, Deal.Stages.Quoted)] = static (_, input) =>
                string.IsNullOrWhiteSpace(input.PriceListVersion)
                    ? DealTransitionStatus.PriceListVersionRequired
                    : null,

            [(Deal.Stages.Quoted, Deal.Stages.Negotiation)] = static (_, _) => null,

            // Rule 1: won requires at least one line with a price AND a quantity. Rule 3: a discount above
            // the tenant threshold cannot advance on the agent's say-so — it holds in negotiation with a
            // pending approval until a lead with deals.discount.approve clears it.
            [(Deal.Stages.Negotiation, Deal.Stages.Won)] = static (deal, input) =>
            {
                if (!deal.Lines.Any(l => l.UnitPrice > 0m && l.Quantity > 0))
                {
                    return DealTransitionStatus.NoPricedLine;
                }

                // The threshold is resolved on the tenant and supplied by the service. A null threshold means
                // the discount check is not applied — a direct-transition caller not exercising rule 3. When a
                // threshold is present and the deal's discount exceeds it, the move is held for a lead's
                // approval unless that approval has already been recorded (IsDiscountApproved). The agent's own
                // permission never clears this; only deals.discount.approve does.
                if (input.DiscountThresholdPct is { } threshold
                    && deal.DiscountPct > threshold
                    && !deal.IsDiscountApproved)
                {
                    return DealTransitionStatus.DiscountApprovalRequired;
                }

                return null;
            },

            // Rule 4: lost requires a reason code ("no reason" was the most expensive missing field).
            [(Deal.Stages.Negotiation, Deal.Stages.Lost)] = static (_, input) =>
                string.IsNullOrWhiteSpace(input.Reason)
                    ? DealTransitionStatus.ReasonRequired
                    : null,
        };

    /// <summary>Whether <paramref name="to"/> is a legal edge out of <paramref name="from"/>, ignoring
    /// guards. Terminal states have no legal edges, so this is <c>false</c> for any source of <c>won</c> or
    /// <c>lost</c>.</summary>
    public static bool IsLegal(string from, string to) => Edges.ContainsKey((from, to));

    /// <summary>
    /// Decides whether <paramref name="deal"/> may move to <paramref name="to"/>: <see cref="DealTransitionStatus.IllegalTransition"/>
    /// if there is no such edge (an unknown target stage, a non-adjacent jump, or any move out of a terminal
    /// state), otherwise the guard's verdict — a failing <see cref="DealTransitionStatus"/> or
    /// <see cref="DealTransitionStatus.Transitioned"/> when the guard passes. The machine does not change the
    /// deal; the caller applies the effect only when this returns <see cref="DealTransitionStatus.Transitioned"/>.
    /// </summary>
    public static DealTransitionStatus Evaluate(Deal deal, string to, DealTransitionInput input)
    {
        ArgumentNullException.ThrowIfNull(deal);

        return Edges.TryGetValue((deal.Stage, to), out var guard)
            ? guard(deal, input) ?? DealTransitionStatus.Transitioned
            : DealTransitionStatus.IllegalTransition;
    }
}

/// <summary>
/// What a transition needs beyond the target stage: the lost reason code (rule 4), the price-list version
/// to freeze at <c>quoted</c> (rule 2), and the tenant discount threshold the move into <c>won</c> is
/// checked against (rule 3). All are optional in general and read only by the specific edge whose guard
/// needs them — a null <see cref="Reason"/> is fine for every edge except the one into <c>lost</c>, a null
/// <see cref="PriceListVersion"/> for every edge except the one into <c>quoted</c>, and a null
/// <see cref="DiscountThresholdPct"/> skips the over-threshold check entirely (used by direct-transition
/// callers not exercising the discount path; the service always supplies the resolved tenant threshold).
/// </summary>
public sealed record DealTransitionInput(
    string? Reason = null,
    string? PriceListVersion = null,
    decimal? DiscountThresholdPct = null);

/// <summary>
/// The verdict on a transition attempt. Every failing value is a domain outcome the caller maps to a status
/// code (illegal/terminal and the three guard failures are 422; concurrency is a separate 409 raised at the
/// persistence boundary, not here) — none of them is an exception, because an illegal transition is an
/// expected answer, not a bug.
/// </summary>
public enum DealTransitionStatus
{
    /// <summary>The edge is legal and its guard passed; the deal may advance.</summary>
    Transitioned = 1,

    /// <summary>No legal edge from the current stage to the requested one — an unknown stage, a non-adjacent
    /// jump, or any move out of the terminal <c>won</c>/<c>lost</c>.</summary>
    IllegalTransition = 2,

    /// <summary>Rule 1: <c>won</c> was requested but no line has both a price and a quantity.</summary>
    NoPricedLine = 3,

    /// <summary>Rule 4: <c>lost</c> was requested with no reason code.</summary>
    ReasonRequired = 4,

    /// <summary>Rule 2: <c>quoted</c> was requested with no price-list version to freeze.</summary>
    PriceListVersionRequired = 5,

    /// <summary>Rule 3: <c>won</c> was requested with a discount above the tenant threshold and no approval on
    /// record. Unlike the other failures this is not a plain rejection — the deal records a pending approval
    /// and stays in <c>negotiation</c>; a lead with <c>deals.discount.approve</c> must clear it before the
    /// move can succeed.</summary>
    DiscountApprovalRequired = 6,
}

/// <summary>The outcome of <see cref="Deal.Transition"/>: the machine's verdict plus the stages it moved
/// between, so a caller can audit "from → to" whether or not the move was allowed.</summary>
public readonly record struct DealTransitionResult(DealTransitionStatus Status, string FromStage, string ToStage)
{
    public bool Succeeded => Status == DealTransitionStatus.Transitioned;
}
