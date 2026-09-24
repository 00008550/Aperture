namespace Aperture.SharedKernel.Domain;

/// <summary>
/// An aggregate's input rule was violated by the caller (ARCHITECTURE.md §5 "Errors are contracts").
/// <para>
/// Thrown by an aggregate's own guards — the single definition of what valid input is — and mapped by the
/// host to a <c>400</c> <c>ValidationProblemDetails</c> naming <see cref="Field"/>. It is deliberately not an
/// <see cref="ArgumentException"/>: that type is also thrown by framework and programming errors, which
/// must stay <c>500</c>s rather than become "your fault" responses.
/// </para>
/// </summary>
public sealed class DomainValidationException : Exception
{
    public DomainValidationException(string field, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        Field = field;
    }

    /// <summary>The offending input, camelCased to match its JSON property on the request.</summary>
    public string Field { get; }
}
