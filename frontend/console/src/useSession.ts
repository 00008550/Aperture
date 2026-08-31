import { useQuery } from '@tanstack/react-query';
import { ApiError, api, type Session } from './api';
import { clearAccessToken, useAccessToken } from './auth';
import type { Permission } from './permissions';

/**
 * Server state lives in TanStack Query and nowhere else. A second copy of the session in a
 * global store is how "it works after a refresh" bugs are born.
 *
 * The query is keyed on the token, so signing in as somebody else cannot be answered out of
 * the previous user's cache entry — the single worst cache bug this shape can have.
 */
export function useSession() {
  const token = useAccessToken();

  const query = useQuery({
    queryKey: ['session', token],
    queryFn: async () => {
      try {
        return await api<Session>('/api/me');
      } catch (error) {
        // A rejected token is not a transient error: drop it so the console returns to
        // sign-in instead of retrying a credential the API has already refused.
        if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
          clearAccessToken(
            'That token was refused. It may have expired, or the account may no longer be an ' +
              'active member of the tenant it names.',
          );
        }
        throw error;
      }
    },
    enabled: token !== null,
    retry: false,
    staleTime: 5 * 60_000,
  });

  /**
   * Fail closed: no session, no permission. `?? false` here is narrowing, not widening —
   * the value being defaulted is "may I", and the default is no.
   */
  const can = (permission: Permission): boolean =>
    query.data?.permissions.includes(permission) ?? false;

  return { ...query, can };
}
