using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Domain;

/// <summary>
/// A person, globally — <b>not</b> tenant-owned.
/// <para>
/// One human who works with two of the platform's regional companies is one identity with two
/// <see cref="Membership"/> rows, not two accounts with the same password. Duplicating the
/// identity per tenant is what makes "reset my password" ambiguous and makes an offboarding
/// miss one of the copies.
/// </para>
/// <para>
/// The cost is that this table is the one place a query must never be tenant-filtered, so it is
/// reachable only through a membership lookup. See ARCHITECTURE.md §2.
/// </para>
/// </summary>
public sealed class User
{
    private User()
    {
    }

    public User(UserId id, string email, string displayName)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public UserId Id { get; private set; }

    /// <summary>Unique platform-wide, stored lower-cased — it is the login.</summary>
    public string Email { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
}
