import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { ApiError, api } from '../api';
import { setAccessToken } from '../auth';

/**
 * A string that exists only in this module. `npm run build` greps `dist/` for it
 * (`scripts/assert-no-dev-picker.mjs`) and fails if a production bundle ever carries the picker.
 */
export const DEV_PICKER_MARKER = 'aperture-dev-signin-picker';

/** `GET /api/dev/users` — `DevUsersResponse` in DevEndpoints.cs. */
export interface DevUser {
  userId: string;
  email: string;
  displayName: string;
  demonstrates: string;
}

export interface DevUsers {
  tenantId: string;
  tenantSlug: string;
  tenantName: string;
  users: DevUser[];
}

interface DevToken {
  accessToken: string;
  expiresAt: string;
}

function describeFailure(error: unknown): string {
  if (error instanceof ApiError && error.status === 404) {
    return 'No demo tenant here. Seed it with: dotnet run --project src/Aperture.Api -- --seed-demo. Or paste a token above.';
  }
  return 'The development sign-in endpoint is unreachable. Is the API running on :5080? You can still paste a token above.';
}

/**
 * Development-only (010-P5a): one click signs in as a seeded demo user. It asks the API's
 * Development-only token endpoint for a token and stores it through the same `setAccessToken()`
 * the paste form uses — there is no parallel auth path, and the token carries identity only, so
 * what the user may do still comes from `GET /api/me`. `SignIn` loads this module only when
 * `import.meta.env.DEV`, so production bundles never contain it.
 */
export default function DevSignInPicker() {
  const [pending, setPending] = useState<string | null>(null);

  const users = useQuery({
    queryKey: ['dev', 'users'],
    queryFn: async () => {
      const body = await api<DevUsers>('/api/dev/users');
      // Something answered, but not the dev endpoint (a proxy page, another server on :5080): treat it
      // as unreachable rather than rendering a shape we do not have.
      if (!Array.isArray(body?.users)) throw new Error('Unexpected /api/dev/users response');
      return body;
    },
    retry: false,
  });

  const mint = useMutation({
    mutationFn: (user: DevUser) =>
      api<DevToken>('/api/dev/token', {
        method: 'POST',
        body: JSON.stringify({ tenantId: users.data?.tenantId, userId: user.userId }),
      }),
    onMutate: (user) => setPending(user.userId),
    onSuccess: (token) => setAccessToken(token.accessToken),
    onSettled: () => setPending(null),
  });

  return (
    <section className={`card dev-picker ${DEV_PICKER_MARKER}`} aria-labelledby="dev-picker-title">
      <h2 id="dev-picker-title">
        Development sign-in <span className="pill">dev only</span>
      </h2>

      {users.isPending && <p className="sub">Looking for demo users…</p>}

      {users.isError && (
        <p className="warn" role="alert">
          {describeFailure(users.error)}
        </p>
      )}

      {users.data && (
        <>
          <p className="sub">
            Sign in as a demo user of <strong>{users.data.tenantName}</strong>. Each one shows a different
            access state.
          </p>
          <ul className="dev-picker-list">
            {users.data.users.map((user) => (
              <li key={user.userId}>
                <button
                  type="button"
                  className="dev-picker-user"
                  disabled={pending !== null}
                  aria-busy={pending === user.userId}
                  onClick={() => mint.mutate(user)}
                >
                  <span className="dev-picker-name">Sign in as {user.displayName}</span>
                  <span className="dev-picker-label">{user.demonstrates}</span>
                  <span className="dev-picker-email mono">{user.email}</span>
                </button>
              </li>
            ))}
          </ul>
        </>
      )}

      {mint.isError && (
        <p className="warn" role="alert">
          {mint.error instanceof ApiError && mint.error.status === 404
            ? 'That user could not be signed in: no active membership in the demo tenant.'
            : describeFailure(mint.error)}
        </p>
      )}
    </section>
  );
}
