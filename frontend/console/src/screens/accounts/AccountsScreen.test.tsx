import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import App from '../../App';
import type { AccountView, Page, Session } from '../../api';
import { setAccessToken } from '../../auth';
import { Permissions } from '../../permissions';

/**
 * P4 — the Accounts screen, exercised through the real `App` (BrowserRouter + route guard +
 * hooks), with only `fetch` stubbed. Test names are the plan's edge cases, verbatim in spirit.
 */

const session = (over: Partial<Session> = {}): Session => ({
  tenantId: '11111111-1111-1111-1111-111111111111',
  userId: '22222222-2222-2222-2222-222222222222',
  email: 'agent@northwind.example',
  displayName: 'Agent',
  permissions: [Permissions.AccountsRead, Permissions.AccountsWrite],
  scopes: [{ kind: 'Own', targetId: null }],
  ...over,
});

const account = (id: string, over: Partial<AccountView> = {}): AccountView => ({
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
  ...over,
});

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

type Route = (url: URL, init?: RequestInit) => Response | Promise<Response>;

/** `/api/me` answers `me` (a session, a status, or a never-settling promise); the rest go to `route`. */
function stubFetch(me: Session | number | 'never', route: Route = () => json({}, 404)) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    if (url.pathname === '/api/me') {
      if (me === 'never') return new Promise<Response>(() => {});
      return typeof me === 'number' ? json({}, me) : json(me);
    }
    return route(url, init);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const requests = (fetchMock: ReturnType<typeof vi.fn>, method: string, path: string) =>
  fetchMock.mock.calls.filter(([input, init]) => {
    const url = new URL(String(input), 'http://localhost');
    return url.pathname === path && ((init as RequestInit | undefined)?.method ?? 'GET') === method;
  });

const bodyOf = (call: unknown[]) =>
  JSON.parse(String((call[1] as RequestInit).body)) as Record<string, unknown>;

function renderAt(path: string) {
  window.history.pushState({}, '', path);
  setAccessToken('t-1');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return render(
    <QueryClientProvider client={client}>
      <App />
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  window.history.pushState({}, '', '/');
});

describe('Accounts route guard', () => {
  it('Given a user without accounts.read, when they reach /accounts, then the screen shows the locked affordance and issues no GET /api/accounts (edge 6)', async () => {
    const fetchMock = stubFetch(session({ permissions: [Permissions.DealsRead] }));
    renderAt('/accounts');

    const locked = await screen.findByTestId('route-locked');
    await waitFor(() => expect(locked).toHaveTextContent('You do not have access'));
    expect(locked).toHaveTextContent(`Requires ${Permissions.AccountsRead}`);
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(requests(fetchMock, 'GET', '/api/accounts')).toHaveLength(0);
  });

  it('Given a user without deals.read, when they reach /deals, then the screen shows the locked affordance and issues no GET /api/deals (edge 6)', async () => {
    const fetchMock = stubFetch(session());
    renderAt('/deals');

    await waitFor(() =>
      expect(screen.getByTestId('route-locked')).toHaveTextContent(`Requires ${Permissions.DealsRead}`),
    );
    expect(requests(fetchMock, 'GET', '/api/deals')).toHaveLength(0);
  });

  it('Given the session has not resolved, when /accounts is reached, then the guard denies fail-closed and issues no GET', async () => {
    const fetchMock = stubFetch('never');
    renderAt('/accounts');

    const locked = screen.getByTestId('route-locked');
    expect(locked).toHaveTextContent('Checking access');
    expect(locked).toHaveTextContent(`Requires ${Permissions.AccountsRead}`);
    // Give any would-be query a chance to fire; none may.
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(requests(fetchMock, 'GET', '/api/accounts')).toHaveLength(0);
  });

  it('Given the session request failed, when /accounts is reached, then the guard still denies and issues no GET', async () => {
    const fetchMock = stubFetch(500);
    renderAt('/accounts');

    await waitFor(() =>
      expect(screen.getByTestId('route-locked')).toHaveTextContent('You do not have access'),
    );
    expect(requests(fetchMock, 'GET', '/api/accounts')).toHaveLength(0);
  });

  it('Given accounts.read, when /accounts is reached, then the grid mounts and the Accounts nav item is current', async () => {
    stubFetch(session(), () => json({ items: [account('a1')], nextCursor: null }));
    renderAt('/accounts');

    expect(await screen.findByRole('table', { name: 'Accounts' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /^Accounts$/ })).toHaveAttribute('aria-current', 'page');
  });
});

describe('Accounts grid', () => {
  it('Given a session with zero scopes, when the accounts grid loads, then it shows the stated "nothing is visible" surface, not an empty table (edge 5)', async () => {
    stubFetch(session({ scopes: [] }), () => json({ items: [], nextCursor: null }));
    renderAt('/accounts');

    const status = await screen.findByText('No data scopes granted.');
    expect(status.closest('[role="status"]')).toHaveTextContent('nothing is visible to you at all');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByText('No accounts yet.')).not.toBeInTheDocument();
  });

  it('Given scopes but no rows, when the grid loads, then it says "No accounts yet", distinct from the no-scope surface', async () => {
    stubFetch(session(), () => json({ items: [], nextCursor: null }));
    renderAt('/accounts');

    expect(await screen.findByText('No accounts yet.')).toBeInTheDocument();
    expect(screen.queryByText('No data scopes granted.')).not.toBeInTheDocument();
  });

  it('Given a grid with a NextCursor, when "load more" is used, then the next page appends and the cursor advances; a null cursor hides the control (edge 11)', async () => {
    const pages: Record<string, Page<AccountView>> = {
      first: { items: [account('a1'), account('a2')], nextCursor: 'c-2' },
      'c-2': { items: [account('a3')], nextCursor: null },
    };
    const fetchMock = stubFetch(session(), (url) =>
      json(pages[url.searchParams.get('cursor') ?? 'first']!),
    );
    renderAt('/accounts');

    const table = await screen.findByRole('table', { name: 'Accounts' });
    expect(within(table).getAllByRole('row')).toHaveLength(3); // header + 2
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));

    await waitFor(() => expect(within(table).getAllByRole('row')).toHaveLength(4));
    expect(screen.getByText('Account a1')).toBeInTheDocument();
    expect(screen.getByText('Account a3')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();

    const lists = requests(fetchMock, 'GET', '/api/accounts').map(
      ([input]) => new URL(String(input), 'http://localhost').searchParams,
    );
    expect(lists.map((p) => p.get('cursor'))).toEqual([null, 'c-2']);
    expect(lists[0]!.get('limit')).toBe('25');
  });

  it('Given the list request fails, when the grid renders, then it shows an error with a retry that refetches', async () => {
    let fail = true;
    const fetchMock = stubFetch(session(), () =>
      fail ? json({}, 500) : json({ items: [account('a1')], nextCursor: null }),
    );
    renderAt('/accounts');

    expect(await screen.findByText('The accounts could not be loaded.')).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Account a1')).toBeInTheDocument();
    expect(requests(fetchMock, 'GET', '/api/accounts')).toHaveLength(2);
  });

  it('Given a row, when it is selected by keyboard, then it is marked selected and the URL names it', async () => {
    stubFetch(session(), (url) =>
      url.pathname === '/api/accounts'
        ? json({ items: [account('a1'), account('a2')], nextCursor: null })
        : json(account('a2')),
    );
    renderAt('/accounts');

    const row = (await screen.findByText('Account a2')).closest('tr')!;
    row.focus();
    await userEvent.keyboard('{Enter}');

    await waitFor(() => expect(row).toHaveAttribute('aria-selected', 'true'));
    expect(window.location.pathname).toBe('/accounts/a2');
  });
});

describe('Account create', () => {
  const fill = async () => {
    await userEvent.click(await screen.findByRole('button', { name: 'New account' }));
    await userEvent.type(screen.getByLabelText('Name'), 'Northwind');
    await userEvent.type(screen.getByLabelText('Tax ID'), 'NW-1');
  };

  it('Given a create form, when the button is clicked twice fast, then only one request is in flight (edge 10)', async () => {
    const fetchMock = stubFetch(session(), (_url, init) =>
      init?.method === 'POST'
        ? new Promise<Response>(() => {}) // stays in flight
        : json({ items: [], nextCursor: null }),
    );
    renderAt('/accounts');
    await fill();

    const submit = screen.getByRole('button', { name: 'Create account' });
    fireEvent.click(submit);
    fireEvent.click(submit);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Creating…' })).toBeDisabled());
    expect(requests(fetchMock, 'POST', '/api/accounts')).toHaveLength(1);
  });

  it('Given a duplicate tax id, when create answers 409, then the server message is shown', async () => {
    stubFetch(session(), (_url, init) =>
      init?.method === 'POST'
        ? json({ error: 'An account with this tax identifier already exists.' }, 409)
        : json({ items: [], nextCursor: null }),
    );
    renderAt('/accounts');
    await fill();
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'An account with this tax identifier already exists.',
    );
  });

  it.each([
    [400, { title: 'One or more validation errors occurred.', errors: { Name: ['Name is too long.'] } }, 'Name is too long.'],
    [422, { error: 'Payment terms exceed the tenant maximum.' }, 'Payment terms exceed the tenant maximum.'],
  ])('Given the server answers %i, when create is submitted, then its message is shown', async (status, body, message) => {
    stubFetch(session(), (_url, init) =>
      init?.method === 'POST' ? json(body, status) : json({ items: [], nextCursor: null }),
    );
    renderAt('/accounts');
    await fill();
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(message);
  });

  it('Given a valid form, when create succeeds, then the new account is selected and the grid refetches', async () => {
    let created = false;
    const fetchMock = stubFetch(session(), (url, init) => {
      if (init?.method === 'POST') {
        created = true;
        return json(account('new', { name: 'Northwind' }), 201);
      }
      if (url.pathname === '/api/accounts/new') return json(account('new', { name: 'Northwind' }));
      return json({ items: created ? [account('new', { name: 'Northwind' })] : [], nextCursor: null });
    });
    renderAt('/accounts');
    await fill();
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }));

    await waitFor(() => expect(window.location.pathname).toBe('/accounts/new'));
    expect(bodyOf(requests(fetchMock, 'POST', '/api/accounts')[0]!)).toMatchObject({
      name: 'Northwind',
      taxId: 'NW-1',
      creditLimit: 0,
      paymentTermsDays: 30,
      regionId: null,
      teamId: null,
    });
    await waitFor(() => expect(requests(fetchMock, 'GET', '/api/accounts').length).toBeGreaterThan(1));
  });

  it('Given a user without accounts.write, when the screen renders, then "New account" is disabled, not hidden', async () => {
    stubFetch(session({ permissions: [Permissions.AccountsRead] }), () =>
      json({ items: [], nextCursor: null }),
    );
    renderAt('/accounts');

    const button = await screen.findByRole('button', { name: 'New account' });
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute('title', `Requires ${Permissions.AccountsWrite}`);
  });
});

describe('Account edit', () => {
  it('Given an account loaded at version N, when saved, then PATCH round-trips expectedVersion N', async () => {
    const fetchMock = stubFetch(session(), (url, init) => {
      if (init?.method === 'PATCH') return json(account('a1', { name: 'Renamed', version: 8 }));
      if (url.pathname === '/api/accounts/a1') return json(account('a1'));
      return json({ items: [account('a1')], nextCursor: null });
    });
    renderAt('/accounts/a1');

    const name = await screen.findByDisplayValue('Account a1');
    await userEvent.clear(name);
    await userEvent.type(name, 'Renamed');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    const patch = requests(fetchMock, 'PATCH', '/api/accounts/a1');
    expect(patch).toHaveLength(1);
    expect(bodyOf(patch[0]!)).toMatchObject({
      name: 'Renamed',
      expectedVersion: 7,
      ownerUserId: '22222222-2222-2222-2222-222222222222',
    });
  });

  it('Given account detail loaded at version N, when PATCH returns 409, then the UI shows "changed by someone else", refetches, and does not resubmit silently (edge 7)', async () => {
    let detailReads = 0;
    let patches = 0;
    const fetchMock = stubFetch(session(), (url, init) => {
      if (init?.method === 'PATCH') {
        patches += 1;
        return patches === 1
          ? json({ error: 'The account was modified by someone else; reload and retry.' }, 409)
          : json(account('a1', { name: 'Mine', version: 10 }));
      }
      if (url.pathname === '/api/accounts/a1') {
        detailReads += 1;
        // Someone else renamed it and bumped the version between our read and our write.
        return json(detailReads === 1 ? account('a1') : account('a1', { name: 'Theirs', version: 9 }));
      }
      return json({ items: [account('a1')], nextCursor: null });
    });
    renderAt('/accounts/a1');

    const name = await screen.findByDisplayValue('Account a1');
    await userEvent.clear(name);
    await userEvent.type(name, 'Mine');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    // The conflict state, carrying the server's own words (the body was kept, not discarded).
    const conflict = await screen.findByTestId('conflict');
    expect(conflict).toHaveTextContent('Changed by someone else.');
    expect(conflict).toHaveTextContent('The account was modified by someone else; reload and retry.');

    // It refetched the account, kept the user's draft, and says what moved.
    await waitFor(() => expect(conflict).toHaveTextContent('now version 9'));
    expect(detailReads).toBeGreaterThanOrEqual(2);
    expect(conflict).toHaveTextContent('Name');
    expect(screen.getByLabelText('Name')).toHaveValue('Mine');

    // Not resubmitted silently: still exactly one PATCH, and the plain save is withdrawn.
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(requests(fetchMock, 'PATCH', '/api/accounts/a1')).toHaveLength(1);
    expect(screen.queryByRole('button', { name: 'Save changes' })).not.toBeInTheDocument();

    // Only an explicit re-apply sends again — against the version the user can now see.
    await userEvent.click(screen.getByRole('button', { name: 'Re-apply my changes' }));
    await screen.findByText('Saved.');
    const sent = requests(fetchMock, 'PATCH', '/api/accounts/a1');
    expect(sent).toHaveLength(2);
    expect(bodyOf(sent[1]!)).toMatchObject({ name: 'Mine', expectedVersion: 9 });
  });

  it('Given a 409 conflict, when the user discards, then the form shows the server copy and the plain save returns', async () => {
    let detailReads = 0;
    stubFetch(session(), (url, init) => {
      if (init?.method === 'PATCH') return json({ error: 'stale' }, 409);
      if (url.pathname === '/api/accounts/a1') {
        detailReads += 1;
        return json(detailReads === 1 ? account('a1') : account('a1', { name: 'Theirs', version: 9 }));
      }
      return json({ items: [account('a1')], nextCursor: null });
    });
    renderAt('/accounts/a1');

    const name = await screen.findByDisplayValue('Account a1');
    await userEvent.clear(name);
    await userEvent.type(name, 'Mine');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(screen.getByTestId('conflict')).toHaveTextContent('now version 9'));

    await userEvent.click(screen.getByRole('button', { name: 'Discard mine, keep theirs' }));

    expect(screen.getByLabelText('Name')).toHaveValue('Theirs');
    expect(screen.queryByTestId('conflict')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled();
  });
});
