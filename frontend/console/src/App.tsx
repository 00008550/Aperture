import { useSession } from './useSession';
import { Permissions, type Permission } from './permissions';

const NAV: { label: string; permission: Permission }[] = [
  { label: 'Accounts', permission: Permissions.AccountsRead },
  { label: 'Deals', permission: Permissions.DealsRead },
  { label: 'Orders', permission: Permissions.OrdersRead },
  { label: 'Administration', permission: Permissions.AdminUsers },
];

export default function App() {
  const { data, isPending, error, can } = useSession();

  return (
    <div className="shell">
      <aside className="side">
        <div className="brand">
          Aperture
          <small>order &amp; deal desk</small>
        </div>
        <nav>
          <a href="#" aria-current="page">
            Overview
          </a>
          {NAV.map((item) => (
            // Denied items render disabled rather than vanishing, so the shape of the
            // product is legible to every role. The server denies regardless.
            <a key={item.label} href="#" data-denied={!can(item.permission)}>
              {item.label}
            </a>
          ))}
        </nav>
      </aside>

      <main>
        <h1>Overview</h1>
        <p className="sub">
          Skeleton shell. The session below comes from <span className="mono">GET /api/me</span>;
          navigation is gated on the permissions it returns.
        </p>

        {isPending && <p className="sub">Loading session…</p>}

        {error && (
          <div className="card">
            <h2>Session</h2>
            <p className="warn">
              Not signed in — <span className="mono">/api/me</span> is unavailable.
            </p>
            <p className="sub">
              Authentication lands in 001-P3. Until then the API answers this route only for an
              authenticated principal, and the console fails closed: no session, no navigation.
            </p>
          </div>
        )}

        {data && (
          <div className="grid">
            <div className="card">
              <h2>Tenant</h2>
              <div>{data.tenantName}</div>
              <div className="mono sub">{data.tenantId}</div>
            </div>
            <div className="card">
              <h2>User</h2>
              <div>{data.displayName}</div>
              <div className="mono sub">{data.userId}</div>
            </div>
            <div className="card">
              <h2>Permissions</h2>
              {data.permissions.map((p) => (
                <span key={p} className="pill mono">
                  {p}
                </span>
              ))}
            </div>
            <div className="card">
              <h2>Data scopes</h2>
              {data.scopes.length === 0 ? (
                <span className="warn">none — fail closed, nothing is visible</span>
              ) : (
                data.scopes.map((s) => (
                  <span key={s} className="pill mono">
                    {s}
                  </span>
                ))
              )}
            </div>
          </div>
        )}
      </main>
    </div>
  );
}
