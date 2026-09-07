import { ApiError } from './api';
import { clearAccessToken, useAccessToken, useSignOutReason } from './auth';
import { BlockField } from './field/BlockField';
import { Navigation } from './Navigation';
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
    // P1 temporary demo mount: the living field renders behind the sign-in surface too. P2 moves
    // the field into a dedicated `app/Shell.tsx` layering layer.
    return (
      <>
        <BlockField />
        <SignIn {...(signOutReason ? { message: signOutReason } : {})} />
      </>
    );
  }

  return (
    <>
      <BlockField />
      <div className="shell">
      <aside className="side">
        <div className="brand">
          Aperture
          <small>order &amp; deal desk</small>
        </div>

        <Navigation can={can} />

        <button type="button" className="link" onClick={() => clearAccessToken()}>
          Sign out
        </button>
      </aside>

      <main>
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
      </main>
      </div>
    </>
  );
}
