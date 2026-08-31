using Aperture.Modules.Access.Domain;
using Aperture.Modules.Access.Persistence;
using Aperture.SharedKernel.Multitenancy;

namespace Aperture.Modules.Access.Auditing;

/// <summary>
/// Writes audit rows through the module's own <see cref="AccessDbContext"/>, so an audit row is a
/// tenant-owned row like any other: the tenant is read from the ambient context and stamped onto
/// the entity, and reads of the trail inherit the same query filter.
/// </summary>
internal sealed class AuditTrail(AccessDbContext db, ITenantContext tenant, TimeProvider clock) : IAuditTrail
{
    public void Record(AuditEntry entry)
    {
        db.AuditEvents.Add(ToEvent(entry));
    }

    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        db.AuditEvents.Add(ToEvent(entry));
        await db.SaveChangesAsync(cancellationToken);
    }

    private AuditEvent ToEvent(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // tenant.TenantId throws when unset. That is deliberate: an audit row with no tenant is
        // worse than none, so we would rather the write fail loudly than land unattributed.
        return new AuditEvent(
            Guid.NewGuid(),
            tenant.TenantId,
            clock.GetUtcNow(),
            entry.Category,
            entry.ActorKind,
            entry.ActorUserId,
            entry.Permission,
            entry.ScopeDecision,
            entry.Reason,
            entry.Action,
            entry.CorrelationId);
    }
}
