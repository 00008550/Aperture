import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  addDealLine,
  approveDealDiscount,
  createDeal,
  getDeal,
  listDeals,
  transitionDeal,
  type AddDealLineRequest,
  type ApproveDiscountRequest,
  type CreateDealRequest,
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

export function useTransitionDeal() {
  return useDealsWrite(
    Permissions.DealsWrite,
    ({ dealId, body }: { dealId: string; body: TransitionDealRequest }) =>
      transitionDeal(dealId, body),
  );
}

export function useApproveDealDiscount() {
  return useDealsWrite(
    Permissions.DealsDiscountApprove,
    ({ dealId, body }: { dealId: string; body: ApproveDiscountRequest }) =>
      approveDealDiscount(dealId, body),
  );
}
