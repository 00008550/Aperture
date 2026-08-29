import { useQuery } from '@tanstack/react-query';
import { api, type Session } from './api';
import type { Permission } from './permissions';

/**
 * Server state lives in TanStack Query and nowhere else. A second copy of the
 * session in a global store is how "it works after a refresh" bugs are born.
 */
export function useSession() {
  const query = useQuery({
    queryKey: ['session'],
    queryFn: () => api<Session>('/api/me'),
    retry: false,
    staleTime: 5 * 60_000,
  });

  const can = (p: Permission) => query.data?.permissions.includes(p) ?? false;
  return { ...query, can };
}
