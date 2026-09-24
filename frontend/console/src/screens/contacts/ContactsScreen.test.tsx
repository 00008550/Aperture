import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import App from '../../App';
import { ApiError, type ContactView, type Session } from '../../api';
import { setAccessToken } from '../../auth';
import { Permissions } from '../../permissions';
import { describeContactError, EMPTY_CONTACT_DRAFT, toCreateContact } from './formModel';

/**
 * P5 — the Contacts screen, exercised through the real `App` (router + guard + hooks) with only
 * `fetch` stubbed. The stub is a tiny stateful server for contacts, so "depart is not delete" is
 * checked against what a re-fetch actually returns, not against client-side filtering.
 */

const ACCOUNT = '33333333-3333-3333-3333-333333333333';
const UNKNOWN_ACCOUNT = '99999999-9999-9999-9999-999999999999';

const session = (over: Partial<Session> = {}): Session => ({
  tenantId: '11111111-1111-1111-1111-111111111111',
  userId: '22222222-2222-2222-2222-222222222222',
  email: 'agent@northwind.example',
  displayName: 'Agent',
  permissions: [Permissions.ContactsRead, Permissions.ContactsWrite],
  scopes: [{ kind: 'Own', targetId: null }],
  ...over,
});

const contact = (id: string, over: Partial<ContactView> = {}): ContactView => ({
  id,
  tenantId: '11111111-1111-1111-1111-111111111111',
  accountId: ACCOUNT,
  ownerUserId: '22222222-2222-2222-2222-222222222222',
  teamId: null,
  regionId: null,
  name: `Contact ${id}`,
  email: `${id}@example.test`,
  phone: null,
  messenger: null,
  isDeparted: false,
  departedAt: null,
  createdAt: '2026-09-01T00:00:00Z',
  ...over,
});

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

type Route = (url: URL, init?: RequestInit) => Response | Promise<Response>;

function stubFetch(me: Session | 'never', route: Route = () => json({}, 404)) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    if (url.pathname === '/api/me') {
      if (me === 'never') return new Promise<Response>(() => {});
      return json(me);
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

/** A stateful contacts server: list honours includeDeparted, depart marks (never removes). */
function contactsServer(initial: ContactView[]) {
  const rows = [...initial];
  const route: Route = (url, init) => {
    const method = init?.method ?? 'GET';
    if (method === 'GET' && url.pathname === '/api/contacts') {
      const include = url.searchParams.get('includeDeparted') === 'true';
      return json({ items: rows.filter((row) => include || !row.isDeparted), nextCursor: null });
    }
    const departMatch = /^\/api\/contacts\/([^/]+)\/depart$/.exec(url.pathname);
    if (method === 'POST' && departMatch) {
      const index = rows.findIndex((row) => row.id === departMatch[1]);
      if (index < 0) return json({}, 404);
      rows[index] = { ...rows[index]!, isDeparted: true, departedAt: '2026-09-20T00:00:00Z' };
      return json(rows[index]);
    }
    return json({}, 404);
  };
  return { rows, route };
}

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

describe('Contacts route guard', () => {
  it('Given a user without contacts.read, when they reach /contacts, then the screen shows the locked affordance and issues no GET /api/contacts (edge 6)', async () => {
    const fetchMock = stubFetch(session({ permissions: [Permissions.AccountsRead] }));
    renderAt('/contacts');

    const locked = await screen.findByTestId('route-locked');
    await waitFor(() => expect(locked).toHaveTextContent('You do not have access'));
    expect(locked).toHaveTextContent(`Requires ${Permissions.ContactsRead}`);
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(requests(fetchMock, 'GET', '/api/contacts')).toHaveLength(0);
  });

  it('Given the session has not resolved, when /contacts is reached, then the guard denies fail-closed and issues no GET', async () => {
    const fetchMock = stubFetch('never');
    renderAt('/contacts');

    expect(screen.getByTestId('route-locked')).toHaveTextContent('Checking access');
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(requests(fetchMock, 'GET', '/api/contacts')).toHaveLength(0);
  });

  it('Given contacts.read, when /contacts is reached, then the grid mounts and the Contacts nav item is current', async () => {
    stubFetch(session(), contactsServer([contact('c1')]).route);
    renderAt('/contacts');

    expect(await screen.findByRole('table', { name: 'Contacts' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /^Contacts$/ })).toHaveAttribute('aria-current', 'page');
  });
});

describe('Contacts grid', () => {
  it('Given a session with zero scopes, when the contacts grid loads, then it shows the stated "nothing is visible" surface, not an empty table (edge 5)', async () => {
    stubFetch(session({ scopes: [] }), contactsServer([]).route);
    renderAt('/contacts');

    const status = await screen.findByText('No data scopes granted.');
    expect(status.closest('[role="status"]')).toHaveTextContent('nothing is visible to you at all');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('Given a grid with a NextCursor, when "load more" is used, then the next page appends and a null cursor hides the control (edge 11)', async () => {
    const fetchMock = stubFetch(session(), (url) =>
      url.searchParams.get('cursor') === 'k2'
        ? json({ items: [contact('c2')], nextCursor: null })
        : json({ items: [contact('c1')], nextCursor: 'k2' }),
    );
    renderAt('/contacts');

    await screen.findByText('Contact c1');
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await screen.findByText('Contact c2');
    expect(screen.getByText('Contact c1')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
    expect(requests(fetchMock, 'GET', '/api/contacts')).toHaveLength(2);
  });

  it('Given a user without contacts.write, when the grid renders, then New contact and Depart are present but disabled', async () => {
    stubFetch(session({ permissions: [Permissions.ContactsRead] }), contactsServer([contact('c1')]).route);
    renderAt('/contacts');

    await screen.findByText('Contact c1');
    expect(screen.getByRole('button', { name: 'New contact' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Depart Contact c1' })).toBeDisabled();
  });
});

describe('Depart', () => {
  it('Given a contact, when departed, then it leaves the active list but remains under includeDeparted, marked departed (edge 12)', async () => {
    const server = contactsServer([contact('c1'), contact('c2')]);
    const fetchMock = stubFetch(session(), server.route);
    renderAt('/contacts');

    await screen.findByText('Contact c1');
    await userEvent.click(screen.getByRole('button', { name: 'Depart Contact c1' }));
    await userEvent.click(screen.getByRole('button', { name: 'Confirm depart' }));

    // The active list re-fetches from the server and no longer carries it…
    await waitFor(() => expect(screen.queryByText('Contact c1')).not.toBeInTheDocument());
    expect(screen.getByText('Contact c2')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('kept for history');
    expect(requests(fetchMock, 'POST', '/api/contacts/c1/depart')).toHaveLength(1);
    // …and depart is not delete: no DELETE was ever sent, and the server still holds the row.
    expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE')).toBe(false);

    await userEvent.click(screen.getByRole('switch', { name: 'Show departed' }));

    const row = await screen.findByTestId('contact-row-c1');
    expect(row).toHaveAttribute('data-departed', 'true');
    expect(within(row).getByTestId('departed-badge')).toHaveTextContent('Departed');
    expect(within(row).queryByRole('button', { name: /Depart/ })).not.toBeInTheDocument();
    expect(screen.getByTestId('contact-row-c2')).toHaveAttribute('data-departed', 'false');
    const lastList = requests(fetchMock, 'GET', '/api/contacts').at(-1)!;
    expect(new URL(String(lastList[0]), 'http://localhost').searchParams.get('includeDeparted')).toBe('true');
  });

  it('Given the depart confirm, when it is clicked twice fast, then only one depart request is in flight (edge 10)', async () => {
    let release: (response: Response) => void = () => {};
    const server = contactsServer([contact('c1')]);
    const fetchMock = stubFetch(session(), (url, init) =>
      init?.method === 'POST'
        ? new Promise<Response>((resolve) => {
            release = resolve;
          })
        : server.route(url, init),
    );
    renderAt('/contacts');

    await screen.findByText('Contact c1');
    fireEvent.click(screen.getByRole('button', { name: 'Depart Contact c1' }));
    const confirm = screen.getByRole('button', { name: 'Confirm depart' });
    fireEvent.click(confirm);
    fireEvent.click(confirm);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Departing…' })).toBeDisabled());
    expect(requests(fetchMock, 'POST', '/api/contacts/c1/depart')).toHaveLength(1);
    release(json(contact('c1', { isDeparted: true })));
  });

  it('Given a contact no longer visible, when depart returns 404, then the grid says so instead of pretending it departed', async () => {
    stubFetch(session(), (_url, init) =>
      init?.method === 'POST' ? json({}, 404) : json({ items: [contact('c1')], nextCursor: null }),
    );
    renderAt('/contacts');

    await screen.findByText('Contact c1');
    await userEvent.click(screen.getByRole('button', { name: 'Depart Contact c1' }));
    await userEvent.click(screen.getByRole('button', { name: 'Confirm depart' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('no longer visible to you');
    expect(screen.queryByText(/kept for history/)).not.toBeInTheDocument();
  });
});

describe('Create under account', () => {
  async function openCreate() {
    await screen.findByRole('table', { name: 'Contacts' });
    await userEvent.click(screen.getByRole('button', { name: 'New contact' }));
    const panel = screen.getByRole('complementary', { name: 'New contact' });
    return panel;
  }

  it('Given an out-of-scope or unknown account, when create returns 404, then the server’s message is surfaced clearly', async () => {
    const fetchMock = stubFetch(session(), (_url, init) =>
      init?.method === 'POST'
        ? json({ error: 'No account with this id is visible to you.' }, 404)
        : json({ items: [contact('c1')], nextCursor: null }),
    );
    renderAt('/contacts');

    const panel = await openCreate();
    await userEvent.type(within(panel).getByLabelText('Account ID'), UNKNOWN_ACCOUNT);
    await userEvent.type(within(panel).getByLabelText('Name'), 'Ada');
    await userEvent.click(within(panel).getByRole('button', { name: 'Create contact' }));

    expect(await within(panel).findByTestId('create-failure')).toHaveTextContent(
      'No account with this id is visible to you.',
    );
    const posts = requests(fetchMock, 'POST', `/api/accounts/${UNKNOWN_ACCOUNT}/contacts`);
    expect(posts).toHaveLength(1);
    // The account travels in the route, never in the body.
    expect(JSON.parse(String((posts[0]![1] as RequestInit).body))).toEqual({
      name: 'Ada',
      email: null,
      phone: null,
      messenger: null,
    });
    // The panel stays open with the draft intact so the user can correct the account.
    expect(within(panel).getByLabelText('Name')).toHaveValue('Ada');
  });

  it('Given a create form, when the button is clicked twice fast, then only one request is in flight (edge 10)', async () => {
    let release: (response: Response) => void = () => {};
    const fetchMock = stubFetch(session(), (_url, init) =>
      init?.method === 'POST'
        ? new Promise<Response>((resolve) => {
            release = resolve;
          })
        : json({ items: [contact('c1')], nextCursor: null }),
    );
    renderAt('/contacts');

    const panel = await openCreate();
    fireEvent.change(within(panel).getByLabelText('Account ID'), { target: { value: ACCOUNT } });
    fireEvent.change(within(panel).getByLabelText('Name'), { target: { value: 'Ada' } });
    const submit = within(panel).getByRole('button', { name: 'Create contact' });
    fireEvent.click(submit);
    fireEvent.click(submit);

    await waitFor(() => expect(within(panel).getByRole('button', { name: 'Creating…' })).toBeDisabled());
    expect(requests(fetchMock, 'POST', `/api/accounts/${ACCOUNT}/contacts`)).toHaveLength(1);
    release(json(contact('c9', { name: 'Ada' }), 201));
  });

  it('Given a selected row, when New contact opens, then the draft is seeded with that row’s account and a success closes the panel and refetches', async () => {
    let created = false;
    const fetchMock = stubFetch(session(), (_url, init) => {
      if (init?.method === 'POST') {
        created = true;
        return json(contact('c9', { name: 'Ada' }), 201);
      }
      return json({ items: created ? [contact('c1'), contact('c9', { name: 'Ada' })] : [contact('c1')], nextCursor: null });
    });
    renderAt('/contacts');

    await userEvent.click(await screen.findByText('Contact c1'));
    const panel = await openCreate();
    expect(within(panel).getByLabelText('Account ID')).toHaveValue(ACCOUNT);
    await userEvent.type(within(panel).getByLabelText('Name'), 'Ada');
    await userEvent.click(within(panel).getByRole('button', { name: 'Create contact' }));

    await screen.findByText('Ada');
    expect(screen.queryByRole('complementary', { name: 'New contact' })).not.toBeInTheDocument();
    expect(requests(fetchMock, 'POST', `/api/accounts/${ACCOUNT}/contacts`)).toHaveLength(1);
  });

  it('Given no accounts.read, when the create panel opens, then no GET /api/accounts is issued for suggestions', async () => {
    const fetchMock = stubFetch(session(), contactsServer([contact('c1')]).route);
    renderAt('/contacts');

    await openCreate();
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(requests(fetchMock, 'GET', '/api/accounts')).toHaveLength(0);
  });
});

describe('contact form model', () => {
  it('requires an account id shaped like a GUID and a name, and sends no request otherwise', () => {
    const result = toCreateContact({ ...EMPTY_CONTACT_DRAFT, accountId: 'nope' });
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.problems).toContain('Account must be an account id (a GUID).');
      expect(result.problems).toContain('Name is required.');
    }
  });

  it('maps blank optional fields to null, never to an empty string', () => {
    const result = toCreateContact({ ...EMPTY_CONTACT_DRAFT, accountId: ACCOUNT, name: ' Ada ', phone: '  ' });
    expect(result).toEqual({
      ok: true,
      accountId: ACCOUNT,
      request: { name: 'Ada', email: null, phone: null, messenger: null },
    });
  });

  it('reads a 404 as the account on create, and as the contact on depart', () => {
    expect(describeContactError(new ApiError(404, 'x', null), 'create').message).toBe(
      'No account with this id is visible to you.',
    );
    expect(describeContactError(new ApiError(404, 'x', null), 'depart').message).toBe(
      'This contact is no longer visible to you.',
    );
  });
});
