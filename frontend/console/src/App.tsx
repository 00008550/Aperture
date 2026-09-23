import { BrowserRouter } from 'react-router';
import { ApiError } from './api';
import { useAccessToken, useSignOutReason } from './auth';
import { BlockField } from './field/BlockField';
import { Shell } from './app/Shell';
import { ConsoleRoutes } from './app/router';
import { SessionPanels } from './SessionPanels';
import { SignIn } from './SignIn';
import { useSession } from './useSession';

function describeError(error: unknown): string | undefined {
  if (error instanceof ApiError) {
    return `The API answered ${error.status}. The session could not be loaded.`;
  }
  return error ? 'The API could not be reached.' : undefined;
}

export default function App() {
  const token = useAccessToken();
  const signOutReason = useSignOutReason();
  const { data, isPending, error, can } = useSession();

  // No token, or a token the API has just refused (useSession clears it): sign-in, not a
  // half-rendered shell. There is no state in which the console shows navigation without a
  // session behind it.
  if (token === null) {
    // The living Aurora Glass field renders behind the sign-in surface too. `Shell` owns the field
    // for the signed-in layout; here (no shell yet) the field mounts directly behind the card.
    return (
      <>
        <BlockField />
        <SignIn {...(signOutReason ? { message: signOutReason } : {})} />
      </>
    );
  }

  const overview = (
    <>
      <h1 id="overview">Overview</h1>
      <p className="sub">
        Session from <span className="mono">GET /api/me</span>. Navigation is disabled where the
        permission is missing; the API denies those calls regardless.
      </p>

      {isPending && <p className="sub">Loading session…</p>}

      {error && !isPending && (
        <div className="card">
          <h2>Session</h2>
          <p className="warn" role="alert">
            {describeError(error)}
          </p>
        </div>
      )}

      {data && <SessionPanels session={data} />}
    </>
  );

  // The router lives inside the signed-in branch: there is no route to reach without a session
  // behind it, and every gated route re-asks `can()` (app/router.tsx).
  return (
    <BrowserRouter>
      <Shell can={can}>
        <ConsoleRoutes overview={overview} />
      </Shell>
    </BrowserRouter>
  );
}
