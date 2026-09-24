import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ApiError,
  addDealLine,
  approveDealDiscount,
  isNotPermitted,
  createDeal,
  getDeal,
  listDeals,
  transitionDeal,
  type AddDealLineRequest,
  type ApproveDiscountRequest,
  type CreateDealRequest,
  type DealView,
  type TransitionDealRequest,
} from '../api';
import { useAccessToken } from '../auth';
import { Permissions, type Permission } from '../permissions';
import { deniedLocally, toGridModel, useGate } from './gate';
import { queryKeys } from './keys';

/**
 * Deals server-state. The grid returns deals without lines; `useDeal` reads one deal with its
 * lines. Every write — create, add-line, transition, discount approval — invalidates the whole
 * `deals` namespace so grid and detail both converge on the server's state. A transition that
 * loses a race is the server's 409 (carrying the current deal); it is surfaced, never retried.
 */

interface DealsListOptions {
  limit?: number | undefined;
}

export function useDeals(options: DealsListOptions = {}) {
  const token = useAccessToken();
  const { session, allowed } = useGate(Permissions.DealsRead);
  const params = { limit: options.limit };

  const query = useInfiniteQuery({
    queryKey: queryKeys.deals.list(token, params),
    queryFn: ({ pageParam }) => listDeals({ limit: options.limit, cursor: pageParam }),
    initialPageParam: null as string | null,
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

export function useDeal(id: string | null) {
  const token = useAccessToken();
  const { allowed } = useGate(Permissions.DealsRead);

  return useQuery({
    queryKey: queryKeys.deals.detail(token, id ?? ''),
    queryFn: () => getDeal(id as string),
    enabled: token !== null && allowed && id !== null,
    retry: false,
  });
}

/** One deals write: gated on its permission, invalidating the `deals` namespace on success. */
function useDealsWrite<TVars>(permission: Permission, send: (vars: TVars) => Promise<unknown>) {
  const queryClient = useQueryClient();
  const { allowed } = useGate(permission);

  return useMutation({
    mutationFn: (vars: TVars) => (allowed ? send(vars) : Promise.reject(deniedLocally(permission))),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.deals.all }),
  });
}

export function useCreateDeal() {
  return useDealsWrite(Permissions.DealsWrite, (body: CreateDealRequest) => createDeal(body));
}

export function useAddDealLine() {
  return useDealsWrite(
    Permissions.DealsWrite,
    ({ dealId, body }: { dealId: string; body: AddDealLineRequest }) => addDealLine(dealId, body),
  );
}

/**
 * The deal a lifecycle 409 carries. The transition and approval endpoints answer a lost `xmin`
 * race with the *current* deal in the body; that deal is what the conflict flow shows. A 409
 * without one (approve on a deal with nothing pending answers `{ error }`) is not a version race.
 */
export function dealInConflict(error: unknown): DealView | null {
  if (!(error instanceof ApiError) || error.status !== 409) return null;
  const body = error.body as Partial<DealView> | null;
  return typeof body === 'object' &&
    body !== null &&
    typeof body.id === 'string' &&
    typeof body.stage === 'string' &&
    typeof body.version === 'number'
    ? (body as DealView)
    : null;
}

/**
 * A lifecycle write (transition or discount approval). The server is the authority on the result,
 * so nothing is written to the cache before it answers — no optimistic stage: the lifecycle has no
 * backward edge, so no move is reversible and a guessed "won" would be a lie the rollback has to
 * retract. What the server returns is put straight into the detail cache (the deal it *says* it
 * now holds, including a held `pendingApproval`), and the namespace is invalidated so the grid
 * converges too. A 409 carries the current deal: it is written into the detail cache the same way,
 * so the conflict flow re-applies against what the server holds — and is never resent here.
 * A 403 means the session's permissions may be stale; the session is re-read so `can()` catches up.
 */
function useLifecycleWrite<TVars extends { dealId: string }>(
  permission: Permission,
  send: (vars: TVars) => Promise<DealView>,
) {
  const token = useAccessToken();
  const queryClient = useQueryClient();
  const { allowed } = useGate(permission);

  return useMutation({
    mutationFn: (vars: TVars) => (allowed ? send(vars) : Promise.reject(deniedLocally(permission))),
    onSuccess: (deal, vars) => {
      queryClient.setQueryData(queryKeys.deals.detail(token, vars.dealId), deal);
      return queryClient.invalidateQueries({ queryKey: queryKeys.deals.all });
    },
    onError: (error, vars) => {
      const current = dealInConflict(error);
      if (current) {
        queryClient.setQueryData(queryKeys.deals.detail(token, vars.dealId), current);
        return queryClient.invalidateQueries({ queryKey: queryKeys.deals.all });
      }
      if (isNotPermitted(error)) {
        return queryClient.invalidateQueries({ queryKey: ['session', token] });
      }
      return undefined;
    },
  });
}

export function useTransitionDeal() {
  return useLifecycleWrite(
    Permissions.DealsWrite,
    ({ dealId, body }: { dealId: string; body: TransitionDealRequest }) =>
      transitionDeal(dealId, body),
  );
}

export function useApproveDealDiscount() {
  return useLifecycleWrite(
    Permissions.DealsDiscountApprove,
    ({ dealId, body }: { dealId: string; body: ApproveDiscountRequest }) =>
      approveDealDiscount(dealId, body),
  );
}
