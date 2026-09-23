import type { ReactNode } from 'react';
import { Navigate, Route, Routes } from 'react-router';
import { NAV_ITEMS } from '../Navigation';
import { Permissions, type Permission } from '../permissions';
import { AccountsScreen } from '../screens/accounts/AccountsScreen';
import { useSession } from '../useSession';

/**
 * The console's route table. `react-router` decides *which screen* a URL names; it is never the
 * authority on *whether* the viewer may see it. Every gated route is wrapped in `RequirePermission`,
 * which asks the same fail-closed `can()` the navigation asks — and the API denies regardless.
 */

export interface RequirePermissionProps {
  permission: Permission;
  children: ReactNode;
}

/**
 * The route guard. Fail closed, in the order that matters:
 *
 * - an **unresolved** session (still loading, errored, absent) is a denial — the screen does not
 *   mount, so none of its queries run, and nothing flashes before the answer arrives;
 * - a resolved session without the permission is a denial;
 * - only a resolved session that grants the exact permission mounts the screen.
 *
 * Denial renders the locked affordance (the same "Requires <permission>" wording the navigation
 * uses) and never mounts `children`, so a denied screen issues no GET (edge 6).
 */
export function RequirePermission({ permission, children }: RequirePermissionProps) {
  const session = useSession();

  if (session.can(permission)) return <>{children}</>;

  const resolving = session.isPending && session.fetchStatus !== 'idle';
  return (
    <section className="locked-screen card" data-testid="route-locked" aria-live="polite">
      <h2>{resolving ? 'Checking access' : 'Locked'}</h2>
      <p className="lock-line">
        <span className="lock-glyph" aria-hidden="true">
          ◇
        </span>
        <span>
          {resolving
            ? 'Confirming your permissions before anything is shown.'
            : 'You do not have access to this section.'}
        </span>
      </p>
      <p className="sub mono">Requires {permission}</p>
    </section>
  );
}

/** A section whose screen is scheduled in a later portion — still gated, still honest about it. */
function NotYetBuilt({ label }: { label: string }) {
  return (
    <section className="card">
      <h2>{label}</h2>
      <p className="sub">This screen is not built yet. Its API exists; the console view is coming.</p>
    </section>
  );
}

export interface ConsoleRoutesProps {
  /** The Overview content — the session panels the shell showed before routing existed. */
  overview: ReactNode;
}

export function ConsoleRoutes({ overview }: ConsoleRoutesProps) {
  return (
    <Routes>
      <Route index element={overview} />
      <Route
        path="accounts"
        element={
          <RequirePermission permission={Permissions.AccountsRead}>
            <AccountsScreen />
          </RequirePermission>
        }
      />
      <Route
        path="accounts/:accountId"
        element={
          <RequirePermission permission={Permissions.AccountsRead}>
            <AccountsScreen />
          </RequirePermission>
        }
      />
      {NAV_ITEMS.filter((item) => item.path !== '/accounts').map((item) => (
        <Route
          key={item.path}
          path={item.path.slice(1)}
          element={
            <RequirePermission permission={item.permission}>
              <NotYetBuilt label={item.label} />
            </RequirePermission>
          }
        />
      ))}
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
