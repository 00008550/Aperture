namespace Aperture.Modules.Access.Auditing;

/// <summary>
/// The one way anything writes to the tenant's audit trail. Public because a deny path in the API
/// host and a mutation in this module both feed it; everything behind it stays internal.
/// <para>
/// The row is always stamped with the ambient tenant, never a tenant the caller names. Reaching
/// this with no established tenant throws rather than writing a tenant-less row — a fail-closed
/// audit is the only kind worth having.
/// </para>
/// </summary>
public interface IAuditTrail
{
    /// <summary>
    /// Stages an audit row on the module's unit of work <em>without</em> saving. A mutation and
    /// its audit row then commit or roll back together under the caller's <c>SaveChanges</c>, so
    /// there is never a committed change with no record of it, nor a record of a change that
    /// rolled back.
    /// </summary>
    void Record(AuditEntry entry);

    /// <summary>
    /// Stages and immediately persists an audit row in its own transaction. For deny paths, which
    /// have no accompanying state change to be atomic with — the denial <em>is</em> the event.
    /// </summary>
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
