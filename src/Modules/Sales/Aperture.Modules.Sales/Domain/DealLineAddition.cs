namespace Aperture.Modules.Sales.Domain;

/// <summary>
/// The verdict of <see cref="Deal.AddLine"/>. As with <see cref="DealTransitionStatus"/>, a refusal is an
/// expected domain answer (a well-formed request the deal's current state forbids — a 422 at the edge), not
/// an exception; malformed line input is the separate 400 path (<c>DomainValidationException</c>).
/// </summary>
public enum DealLineAdditionStatus
{
    /// <summary>The line was attached to the deal.</summary>
    Added = 1,

    /// <summary>The deal is <c>won</c> or <c>lost</c>; a closed deal takes no new line.</summary>
    DealClosed = 2,

    /// <summary>The deal has a frozen price-list version (DOMAIN.md §2 rule 2) and the line named a
    /// different one.</summary>
    PriceListVersionMismatch = 3,
}

/// <summary>The outcome of <see cref="Deal.AddLine"/>; <see cref="Line"/> is set only when
/// <see cref="Status"/> is <see cref="DealLineAdditionStatus.Added"/>.</summary>
public readonly record struct DealLineAddition(DealLineAdditionStatus Status, DealLine? Line);
