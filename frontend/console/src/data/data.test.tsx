import { describe, expect, it, vi, afterEach } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { AccountView, DealView, Page, Session } from '../api';
import { getAccessToken, getSignOutReason, setAccessToken } from '../auth';
import { Permissions } from '../permissions';
import { toGridModel } from './gate';
import { queryKeys } from './keys';
import { useAccounts, useUpdateAccount } from './useAccounts';
import { useContacts } from './useContacts';
import { useAddDealLine, useDeals, useTransitionDeal } from './useDeals';

const session = (over: Partial<Session> = {}): Session => ({
  tenantId: '11111111-1111-1111-1111-111111111111',
  userId: '22222222-2222-2222-2222-222222222222',
  email: 'agent@northwind.example',
  displayName: 'Agent',
  permissions: [
    Permissions.AccountsRead,
    Permissions.AccountsWrite,
    Permissions.ContactsRead,
    Permissions.DealsRead,
    Permissions.DealsWrite,
  ],
  scopes: [{ kind: 'Own', targetId: null }],
  ...over,
});

const account = (id: string): AccountView => ({
  id,
  tenantId: '11111111-1111-1111-1111-111111111111',
  ownerUserId: '22222222-2222-2222-2222-222222222222',
  name: `Account ${id}`,
  taxId: `TAX-${id}`,
  creditLimit: 1000,
  paymentTermsDays: 30,
  regionId: null,
  teamId: null,
  accountId: id,
  createdAt: '2026-09-01T00:00:00Z',
  version: 7,
});

const dealRow = (id: string): DealView => ({
  id,
  tenantId: '11111111-1111-1111-1111-111111111111',
  accountId: 'a1',
  accountName: null,
  ownerUserId: '22222222-2222-2222-2222-222222222222',
  teamId: null,
  regionId: null,
  name: `Deal ${id}`,
  stage: 'quoted',
  amount: 100,
  discountPct: 0,
  frozenPriceListVersion: 'PL-1',
  pendingApproval: false,
  lostReasonCode: null,
  createdAt: '2026-09-01T00:00:00Z',
  version: 3,
  lines: [],
});

type Route = (url: URL, init?: RequestInit) => Response | Promise<Response>;

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

/** Answers /api/me with `me`, and every other path with `route`. Returns the mock for assertions. */
function stubFetch(me: Session, route: Route) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    if (url.pathname === '/api/me') return json(me);
    return route(url, init);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function wrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return { client, Wrapper };
}

const calls = (fetchMock: ReturnType<typeof vi.fn>, path: string) =>
  fetchMock.mock.calls
    .map(([input]) => new URL(String(input), 'http://localhost'))
    .filter((url) => url.pathname === path);

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Sales data layer', () => {
  it('Given a NextCursor, when the next page is fetched, then the cursor advances and a null cursor stops (edge 11)', async () => {
    setAccessToken('t-1');
    const fetchMock = stubFetch(session(), (url) => {
      const cursor = url.searchParams.get('cursor');
      const page: Page<AccountView> =
        cursor === null
          ? { items: [account('a1')], nextCursor: 'c-2' }
          : { items: [account('a2')], nextCursor: null };
      return json(page);
    });
    const { Wrapper } = wrapper();

    const { result } = renderHook(() => useAccounts({ limit: 1 }), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.grid.kind).toBe('rows'));
    expect(result.current.hasNextPage).toBe(true);

    await act(() => result.current.fetchNextPage());

    await waitFor(() => expect(result.current.data?.pages).toHaveLength(2));
    const grid = result.current.grid;
    expect(grid.kind === 'rows' && grid.rows.map((row) => row.id)).toEqual(['a1', 'a2']);
    expect(result.current.hasNextPage).toBe(false);
    expect(grid.kind === 'rows' && grid.hasMore).toBe(false);

    const listCalls = calls(fetchMock, '/api/accounts');
    expect(listCalls).toHaveLength(2);
    expect(listCalls[0]!.searchParams.get('cursor')).toBeNull();
    expect(listCalls[0]!.searchParams.get('limit')).toBe('1');
    expect(listCalls[1]!.searchParams.get('cursor')).toBe('c-2');
  });

  it('Given an account read, when an update succeeds, then the accounts key is invalidated and refetched', async () => {
    setAccessToken('t-1');
    let version = 7;
    const fetchMock = stubFetch(session(), (_url, init) => {
      if (init?.method === 'PATCH') {
        version = 8;
        return json({ ...account('a1'), version });
      }
      return json({ items: [{ ...account('a1'), version }], nextCursor: null });
    });
    const { client, Wrapper } = wrapper();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    const { result } = renderHook(() => ({ list: useAccounts(), update: useUpdateAccount() }), {
      wrapper: Wrapper,
    });
    await waitFor(() => expect(result.current.list.grid.kind).toBe('rows'));

    await act(() =>
      result.current.update.mutateAsync({
        id: 'a1',
        body: {
          ownerUserId: '22222222-2222-2222-2222-222222222222',
          name: 'Renamed',
          creditLimit: 1000,
          paymentTermsDays: 30,
          regionId: null,
          teamId: null,
          expectedVersion: 7,
        },
      }),
    );

    expect(invalidate).toHaveBeenCalledWith({ queryKey: queryKeys.accounts.all });
    await waitFor(() => expect(calls(fetchMock, '/api/accounts')).toHaveLength(2));
    const patch = fetchMock.mock.calls.find(([, init]) => init?.method === 'PATCH');
    expect(JSON.parse(String(patch?.[1]?.body)).expectedVersion).toBe(7);
    await waitFor(() => {
      const grid = result.current.list.grid;
      expect(grid.kind === 'rows' && grid.rows[0]!.version).toBe(8);
    });
  });

  it('Given a deal, when a transition succeeds, then the deals key is invalidated', async () => {
    setAccessToken('t-1');
    stubFetch(session(), () => json({ id: 'd1', stage: 'qualified' } as Partial<DealView>));
    const { client, Wrapper } = wrapper();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    const { result } = renderHook(() => useTransitionDeal(), { wrapper: Wrapper });
    await waitFor(() => expect(client.getQueryData(['session', 't-1'])).toBeDefined());

    await act(() =>
      result.current.mutateAsync({
        dealId: 'd1',
        body: { targetStage: 'qualified', expectedVersion: 3 },
      }),
    );

    expect(invalidate).toHaveBeenCalledWith({ queryKey: queryKeys.deals.all });
  });

  it('Given a signed-in session, when a Sales read answers 401, then the token is cleared to sign-in', async () => {
    setAccessToken('t-1');
    stubFetch(session(), () => new Response(null, { status: 401 }));
    const { Wrapper } = wrapper();

    const { result } = renderHook(() => useDeals(), { wrapper: Wrapper });

    await waitFor(() => expect(getAccessToken()).toBeNull());
    expect(getSignOutReason()).toMatch(/token was refused/);
    expect(result.current.data).toBeUndefined();
  });

  it('Given a signed-in session, when a Sales read answers a policy 403, then the read errors and the user stays signed in', async () => {
    setAccessToken('t-1');
    stubFetch(session(), () => new Response(null, { status: 403 }));
    const { Wrapper } = wrapper();

    const { result } = renderHook(() => useDeals(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.grid.kind).toBe('error'));
    expect(getAccessToken()).toBe('t-1');
  });

  it('Given a server 403 on a lifecycle write, when it answers, then the session is re-read and the token survives', async () => {
    setAccessToken('t-1');
    const fetchMock = stubFetch(session(), () => new Response(null, { status: 403 }));
    const { client, Wrapper } = wrapper();
    const { result } = renderHook(() => useTransitionDeal(), { wrapper: Wrapper });
    await waitFor(() => expect(client.getQueryData(['session', 't-1'])).toBeDefined());
    const sessionReads = calls(fetchMock, '/api/me').length;

    await act(async () => {
      await result.current
        .mutateAsync({ dealId: 'd1', body: { targetStage: 'qualified', expectedVersion: 3 } })
        .catch(() => undefined);
    });

    expect(getAccessToken()).toBe('t-1');
    await waitFor(() => expect(calls(fetchMock, '/api/me').length).toBeGreaterThan(sessionReads));
  });

  it('Given a 409 carrying the current deal, when a transition loses the race, then the detail cache holds the server deal and nothing is resent', async () => {
    setAccessToken('t-1');
    const current = { ...dealRow('d1'), stage: 'negotiation', version: 9 };
    const fetchMock = stubFetch(session(), () =>
      new Response(JSON.stringify(current), { status: 409 }),
    );
    const { client, Wrapper } = wrapper();
    const { result } = renderHook(() => useTransitionDeal(), { wrapper: Wrapper });
    await waitFor(() => expect(client.getQueryData(['session', 't-1'])).toBeDefined());

    await act(async () => {
      await result.current
        .mutateAsync({ dealId: 'd1', body: { targetStage: 'negotiation', expectedVersion: 3 } })
        .catch(() => undefined);
    });

    expect(client.getQueryData(queryKeys.deals.detail('t-1', 'd1'))).toEqual(current);
    expect(calls(fetchMock, '/api/deals/d1/transition')).toHaveLength(1);
  });

  it('Given a 409 carrying the current deal, when an add-line is stale, then the detail cache holds the server deal and nothing is resent', async () => {
    setAccessToken('t-1');
    const current = { ...dealRow('d1'), stage: 'quoted', version: 11 };
    const fetchMock = stubFetch(session(), () =>
      new Response(JSON.stringify(current), { status: 409 }),
    );
    const { client, Wrapper } = wrapper();
    const { result } = renderHook(() => useAddDealLine(), { wrapper: Wrapper });
    await waitFor(() => expect(client.getQueryData(['session', 't-1'])).toBeDefined());

    await act(async () => {
      await result.current
        .mutateAsync({
          dealId: 'd1',
          body: { productRef: 'X', unitPrice: 1, quantity: 1, priceListVersion: null, expectedVersion: 3 },
        })
        .catch(() => undefined);
    });

    expect(client.getQueryData(queryKeys.deals.detail('t-1', 'd1'))).toEqual(current);
    const posts = calls(fetchMock, '/api/deals/d1/lines');
    expect(posts).toHaveLength(1);
  });

  it('Given a session with zero scopes, when a Sales grid loads, then it is the stated no-scope model, not an empty table (edge 5)', async () => {
    setAccessToken('t-1');
    stubFetch(session({ scopes: [] }), () => json({ items: [], nextCursor: null }));
    const { Wrapper } = wrapper();

    const { result } = renderHook(() => useContacts(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.grid.kind).toBe('no-scope'));
  });

  it('Given a session without the read permission, when a grid mounts, then it is denied and issues no GET', async () => {
    setAccessToken('t-1');
    const fetchMock = stubFetch(session({ permissions: [] }), () => json({ items: [], nextCursor: null }));
    const { Wrapper } = wrapper();

    const { result } = renderHook(() => useAccounts(), { wrapper: Wrapper });

    await waitFor(() => expect(calls(fetchMock, '/api/me')).toHaveLength(1));
    expect(result.current.grid.kind).toBe('denied');
    expect(calls(fetchMock, '/api/accounts')).toHaveLength(0);
  });
});

describe('toGridModel', () => {
  const base = { allowed: true, scopeCount: 1, isPending: false, error: null, hasNextPage: false };

  it('Given zero scopes, when the server returns rows anyway, then it is still no-scope — never widened', () => {
    expect(
      toGridModel({ ...base, scopeCount: 0, pages: [{ items: [1], nextCursor: null }] }).kind,
    ).toBe('no-scope');
  });

  it('Given an unresolved session, when judging the grid, then it is loading, not rows', () => {
    expect(toGridModel({ ...base, scopeCount: undefined, pages: undefined }).kind).toBe('loading');
  });

  it('Given scopes and an empty page, when judging the grid, then it is empty (distinct from no-scope)', () => {
    expect(toGridModel({ ...base, pages: [{ items: [], nextCursor: null }] }).kind).toBe('empty');
  });

  it('Given no permission, when judging the grid, then it is denied first', () => {
    expect(toGridModel({ ...base, allowed: false, scopeCount: 0, pages: undefined }).kind).toBe(
      'denied',
    );
  });
});
