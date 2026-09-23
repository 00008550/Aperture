import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ApiError,
  createAccount,
  getAccount,
  listAccounts,
  updateAccount,
  type CreateAccountRequest,
  type UpdateAccountRequest,
} from '../api';
import { useAccessToken } from '../auth';
import { Permissions } from '../permissions';
import { deniedLocally, toGridModel, useGate } from './gate';
import { queryKeys } from './keys';

/**
 * Accounts server-state, and nothing else — TanStack Query is the only home for it (§11). Reads
 * are keyset-paginated: `getNextPageParam` returns the server's `nextCursor`, and when that is
 * `null` it returns `undefined`, which is how TanStack Query knows there is no next page —
 * `hasNextPage` goes false and the "load more" affordance hides (edge 11). Every write hook
 * invalidates the whole `accounts` namespace on success, so the grid and detail refetch from the
 * server — the authority — giving read-your-writes without a second, drift-prone cache copy.
 */

interface AccountsListOptions {
  limit?: number | undefined;
}

export function useAccounts(options: AccountsListOptions = {}) {
  const token = useAccessToken();
  const { session, allowed } = useGate(Permissions.AccountsRead);
  const params = { limit: options.limit };

  const query = useInfiniteQuery({
    queryKey: queryKeys.accounts.list(token, params),
    queryFn: ({ pageParam }) => listAccounts({ limit: options.limit, cursor: pageParam }),
    initialPageParam: null as string | null,
    // null cursor -> undefined -> no next page (edge 11 stops); a real cursor advances the keyset.
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    enabled: token !== null && allowed,
    retry: false,
  });

  const grid = toGridModel({
    allowed,
    scopeCount: session.data?.scopes.length,
    pages: query.data?.pages,
    isPending: query.isPending,
    error: query.error,
    hasNextPage: query.hasNextPage,
  });

  return { ...query, grid };
}

export function useAccount(id: string | null) {
  const token = useAccessToken();
  const { allowed } = useGate(Permissions.AccountsRead);

  return useQuery({
    queryKey: queryKeys.accounts.detail(token, id ?? ''),
    queryFn: () => getAccount(id as string),
    enabled: token !== null && allowed && id !== null,
    retry: false,
  });
}

export function useCreateAccount() {
  const queryClient = useQueryClient();
  const { allowed } = useGate(Permissions.AccountsWrite);

  return useMutation({
    mutationFn: (body: CreateAccountRequest) =>
      allowed ? createAccount(body) : Promise.reject(deniedLocally(Permissions.AccountsWrite)),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.accounts.all }),
  });
}

export function useUpdateAccount() {
  const queryClient = useQueryClient();
  const { allowed } = useGate(Permissions.AccountsWrite);

  return useMutation({
    // The caller round-trips the `expectedVersion` (xmin) it read; a stale value is the server's
    // 409, surfaced to the screen (P4), never silently retried.
    mutationFn: ({ id, body }: { id: string; body: UpdateAccountRequest }) =>
      allowed ? updateAccount(id, body) : Promise.reject(deniedLocally(Permissions.AccountsWrite)),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.accounts.all }),
    // A 409 means our copy is stale: refetch so the screen can show what the server now holds
    // (edge 7). Refetch only — the write is never resubmitted without the user asking again.
    onError: (error) => {
      if (error instanceof ApiError && error.status === 409) {
        return queryClient.invalidateQueries({ queryKey: queryKeys.accounts.all });
      }
      return undefined;
    },
  });
}
