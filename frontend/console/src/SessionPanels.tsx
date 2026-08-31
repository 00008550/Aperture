import type { Session } from './api';

function describeScope(kind: string, targetId: string | null): string {
  return targetId === null ? kind : `${kind}(${targetId.slice(0, 8)})`;
}

/**
 * Tenant, identity, verbs and rows, exactly as `GET /api/me` reported them.
 *
 * The scopes card is the one that matters. DOMAIN.md §5.1 is a report that showed one
 * region's data to another because "no regions selected" was read as "all regions" — so a
 * user with no scopes gets a stated, styled "nothing is visible" rather than an empty list,
 * which is indistinguishable from loading, from "no data yet", and from "unfiltered".
 */
export function SessionPanels({ session }: { session: Session }) {
  const hasScopes = session.scopes.length > 0;

  return (
    <div className="grid">
      <div className="card">
        <h2>Tenant</h2>
        <div className="mono">{session.tenantId}</div>
      </div>

      <div className="card">
        <h2>User</h2>
        <div>{session.displayName}</div>
        <div className="sub">{session.email}</div>
        <div className="mono sub">{session.userId}</div>
      </div>

      <div className="card">
        <h2>Permissions</h2>
        {session.permissions.length === 0 ? (
          <p className="warn" role="status">
            No permissions granted — every action is denied.
          </p>
        ) : (
          session.permissions.map((p) => (
            <span key={p} className="pill mono">
              {p}
            </span>
          ))
        )}
      </div>

      <div className="card" data-testid="scopes">
        <h2>Data scopes</h2>
        {hasScopes ? (
          session.scopes.map((s) => (
            <span key={`${s.kind}:${s.targetId ?? ''}`} className="pill mono">
              {describeScope(s.kind, s.targetId)}
            </span>
          ))
        ) : (
          <div className="empty" role="status">
            <strong className="warn">No data scopes granted.</strong>
            <p className="sub">
              This is not an empty result set — nothing is visible to you at all. Records appear
              only once an administrator grants a scope. Ask for one in Administration.
            </p>
          </div>
        )}
      </div>
    </div>
  );
}
