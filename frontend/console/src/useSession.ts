import { useQuery } from '@tanstack/react-query';
import { apiAuthed, type Session } from './api';
import { useAccessToken } from './auth';
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
    // apiAuthed drops a refused token (401/403) so the console returns to sign-in instead of
    // retrying a credential the API has already rejected — the one shared mechanism every data
    // hook uses too, so there is a single place that decides a token has died.
    queryFn: () => apiAuthed<Session>('/api/me'),
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
