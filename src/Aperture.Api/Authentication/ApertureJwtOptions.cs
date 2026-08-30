namespace Aperture.Api.Authentication;

/// <summary>
/// Bearer-token settings, bound from the <c>Authentication</c> configuration section.
/// <para>
/// There are no defaults. A default issuer or a built-in signing key is a credential in source
/// control that some deployment will keep, so a missing value stops the host at startup rather
/// than producing an API that validates tokens anybody can mint.
/// </para>
/// </summary>
public sealed class ApertureJwtOptions
{
    public const string SectionName = "Authentication";

    /// <summary>Minimum key length for HMAC-SHA256. Shorter keys are rejected by the algorithm
    /// itself; checking here turns a confusing runtime failure into a startup message.</summary>
    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Throws unless every value needed to validate a token is present.</summary>
    public void Validate()
    {
        Require(Issuer, nameof(Issuer));
        Require(Audience, nameof(Audience));
        Require(SigningKey, nameof(SigningKey));

        if (System.Text.Encoding.UTF8.GetByteCount(SigningKey) < MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SigningKey)} must be at least {MinimumSigningKeyBytes} bytes.");
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} is not configured. The API refuses to start without it.");
        }
    }
}
