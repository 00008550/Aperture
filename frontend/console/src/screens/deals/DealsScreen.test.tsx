import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import App from '../../App';
import {
  ApiError,
  type AccountView,
  type DealLineView,
  type DealView,
  type Session,
} from '../../api';
import { setAccessToken } from '../../auth';
import { Permissions } from '../../permissions';
import {
  EMPTY_DEAL_DRAFT,
  EMPTY_LINE_DRAFT,
  describeDealError,
  toAddLine,
  toCreateDeal,
} from './formModel';

/**
 * P6 — the Deals screen, exercised through the real `App` (router + guard + hooks) with only
 * `fetch` stubbed. The stub is a small stateful deals server: the list omits lines (as the real
 * list does) and the single-deal read carries them, so "add-line refetches detail" is checked
 * against what the re-fetch actually returns.
 */

const TENANT = '11111111-1111-1111-1111-111111111111';
const USER = '22222222-2222-2222-2222-222222222222';
const ACCOUNT = '33333333-3333-3333-3333-333333333333';

const session = (over: Partial<Session> = {}): Session => ({
  tenantId: TENANT,
  userId: USER,
  email: 'agent@northwind.example',
  displayName: 'Agent',
  permissions: [Permissions.DealsRead, Permissions.DealsWrite],
  scopes: [{ kind: 'Own', targetId: null }],
  ...over,
});

const deal = (id: string, over: Partial<DealView> = {}): DealView => ({
  id,
  tenantId: TENANT,
  accountId: ACCOUNT,
  ownerUserId: USER,
  teamId: null,
  regionId: null,
  name: `Deal ${id}`,
  stage: 'new',
  amount: 1200,
  discountPct: 5,
  frozenPriceListVersion: null,
  pendingApproval: false,
  lostReasonCode: null,
  createdAt: '2026-09-01T00:00:00Z',
  version: 7,
  lines: [],
  ...over,
});

const line = (id: string, dealId: string, over: Partial<DealLineView> = {}): DealLineView => ({
  id,
  dealId,
  productRef: `SKU-${id}`,
  unitPrice: 10,
  quantity: 3,
  priceListVersion: 'PL-2026',
  ...over,
});

const account = (id: string, name: string): AccountView => ({
  id,
  tenantId: TENANT,
  ownerUserId: USER,
  name,
  taxId: 'TAX-1',
  creditLimit: 0,
  paymentTermsDays: 30,
  regionId: null,
  teamId: null,
  accountId: id,
  createdAt: '2026-09-01T00:00:00Z',
  version: 1,
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

/**
 * A stateful deals server. The list strips lines (the real grid returns `Lines` empty); the
 * single-deal read returns them; add-line appends and returns the updated deal.
 */
function dealsServer(initial: DealView[], accounts: AccountView[] = []) {
  const rows = initial.map((row) => ({ ...row, lines: [...row.lines] }));
  let nextLine = 1;
  const route: Route = async (url, init) => {
    const method = init?.method ?? 'GET';
    if (method === 'GET' && url.pathname === '/api/accounts') {
      return json({ items: accounts, nextCursor: null });
    }
    if (method === 'GET' && url.pathname === '/api/deals') {
      return json({ items: rows.map((row) => ({ ...row, lines: [] })), nextCursor: null });
    }
    if (method === 'POST' && url.pathname === '/api/deals') {
      const body = JSON.parse(String(init?.body)) as { name: string; accountId: string };
      const created = deal(`d${rows.length + 1}`, { name: body.name, accountId: body.accountId });
      rows.push(created);
      return json(created, 201);
    }
    const lineMatch = /^\/api\/deals\/([^/]+)\/lines$/.exec(url.pathname);
    if (method === 'POST' && lineMatch) {
      const target = rows.find((row) => row.id === lineMatch[1]);
      if (!target) return json(null, 404);
      const body = JSON.parse(String(init?.body)) as Omit<DealLineView, 'id' | 'dealId'>;
      target.lines.push({ id: `n${nextLine++}`, dealId: target.id, ...body });
      target.version += 1;
      return json(target);
    }
    const detailMatch = /^\/api\/deals\/([^/]+)$/.exec(url.pathname);
    if (method === 'GET' && detailMatch) {
      const target = rows.find((row) => row.id === detailMatch[1]);
      return target ? json(target) : new Response(null, { status: 404 });
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

describe('Deals route guard', () => {
  it('Given a user without deals.read, when they reach /deals, then the screen shows the locked affordance and issues no GET /api/deals (edge 6)', async () => {
    const fetchMock = stubFetch(session({ permissions: [Permissions.AccountsRead] }));
    renderAt('/deals');

    const locked = await screen.findByTestId('route-locked');
    await waitFor(() => expect(locked).toHaveTextContent('You do not have access'));
    expect(locked).toHaveTextContent(`Requires ${Permissions.DealsRead}`);
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(requests(fetchMock, 'GET', '/api/deals')).toHaveLength(0);
  });

  it('Given a user without deals.read, when they deep-link /deals/{id}, then no GET /api/deals/{id} is issued', async () => {
    const fetchMock = stubFetch(session({ permissions: [] }));
    renderAt('/deals/d1');

    await waitFor(() =>
      expect(screen.getByTestId('route-locked')).toHaveTextContent('You do not have access'),
    );
    expect(requests(fetchMock, 'GET', '/api/deals/d1')).toHaveLength(0);
  });

  it('Given the session has not resolved, when /deals is reached, then the guard denies fail-closed and issues no GET', async () => {
    const fetchMock = stubFetch('never');
    renderAt('/deals');

    expect(screen.getByTestId('route-locked')).toHaveTextContent('Checking access');
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(requests(fetchMock, 'GET', '/api/deals')).toHaveLength(0);
  });

  it('Given deals.read, when /deals is reached, then the grid mounts and the Deals nav item is current', async () => {
    stubFetch(session(), dealsServer([deal('d1')]).route);
    renderAt('/deals');

    expect(await screen.findByRole('table', { name: 'Deals' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /^Deals$/ })).toHaveAttribute('aria-current', 'page');
  });
});

describe('Deals grid', () => {
  it('Given a session with zero scopes, when the deals grid loads, then it shows the stated "nothing is visible" surface, not an empty table (edge 5)', async () => {
    stubFetch(session({ scopes: [] }), dealsServer([]).route);
    renderAt('/deals');

    const status = await screen.findByText('No data scopes granted.');
    expect(status.closest('[role="status"]')).toHaveTextContent('nothing is visible to you at all');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('Given deals, when the grid renders, then each row shows its stage as a chip, the amount and the discount', async () => {
    stubFetch(
      session(),
      dealsServer([deal('d1', { stage: 'negotiation', amount: 48000, discountPct: 12.5 })]).route,
    );
    renderAt('/deals');

    const row = await screen.findByTestId('deal-row-d1');
    expect(within(row).getByTestId('stage-chip')).toHaveAttribute('data-stage', 'negotiation');
    expect(within(row).getByTestId('stage-chip')).toHaveTextContent('negotiation');
    expect(row).toHaveTextContent('48,000.00');
    expect(row).toHaveTextContent('12.5%');
  });

  it('Given a grid with a NextCursor, when "load more" is used, then the next page appends and a null cursor hides the control (edge 11)', async () => {
    const fetchMock = stubFetch(session(), (url) =>
      url.searchParams.get('cursor') === 'k2'
        ? json({ items: [deal('d2')], nextCursor: null })
        : json({ items: [deal('d1')], nextCursor: 'k2' }),
    );
    renderAt('/deals');

    await screen.findByText('Deal d1');
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await screen.findByText('Deal d2');
    expect(screen.getByText('Deal d1')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
    const lists = requests(fetchMock, 'GET', '/api/deals');
    expect(lists).toHaveLength(2);
    expect(new URL(String(lists[1]![0]), 'http://localhost').searchParams.get('cursor')).toBe('k2');
  });

  it('Given a user without deals.write, when the screen renders, then New deal and Add line are present but disabled', async () => {
    stubFetch(
      session({ permissions: [Permissions.DealsRead] }),
      dealsServer([deal('d1', { lines: [line('l1', 'd1')] })]).route,
    );
    renderAt('/deals/d1');

    await screen.findByTestId('deal-detail');
    expect(screen.getByRole('button', { name: 'New deal' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Add line' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Add line' })).toHaveAttribute(
      'title',
      `Requires ${Permissions.DealsWrite}`,
    );
  });
});

describe('Deal detail', () => {
  it('Given a deal with lines, when it is selected, then the detail shows its lines while the grid omits them, and the URL names the deal', async () => {
    const server = dealsServer([
      deal('d1', {
        lines: [
          line('l1', 'd1', { productRef: 'WIDGET-9', quantity: 4, unitPrice: 25, priceListVersion: 'PL-7' }),
          line('l2', 'd1', { priceListVersion: null }),
        ],
      }),
    ]);
    const fetchMock = stubFetch(session(), server.route);
    renderAt('/deals');

    const row = await screen.findByTestId('deal-row-d1');
    // The grid carries no line data at all.
    expect(within(row).queryByText('WIDGET-9')).not.toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Deal lines' })).not.toBeInTheDocument();

    await userEvent.click(row);
    expect(window.location.pathname).toBe('/deals/d1');
    expect(row).toHaveAttribute('aria-selected', 'true');

    const lines = await screen.findByRole('table', { name: 'Deal lines' });
    const first = within(lines).getByTestId('line-l1');
    expect(first).toHaveTextContent('WIDGET-9');
    expect(first).toHaveTextContent('4');
    expect(first).toHaveTextContent('25.00');
    expect(first).toHaveTextContent('100.00'); // line total = qty × unit price
    expect(first).toHaveTextContent('PL-7');
    expect(within(lines).getByTestId('line-l2')).toHaveTextContent('—');
    expect(within(lines).getByTestId('lines-total')).toHaveTextContent('130.00');
    expect(requests(fetchMock, 'GET', '/api/deals/d1')).toHaveLength(1);
  });

  it('Given a deep link to /deals/{id}, when it loads, then the detail opens for that deal', async () => {
    stubFetch(session(), dealsServer([deal('d1'), deal('d2')]).route);
    renderAt('/deals/d2');

    const detail = await screen.findByTestId('deal-detail');
    expect(within(detail).getByRole('heading', { name: 'Deal d2' })).toBeInTheDocument();
    expect(within(detail).getByTestId('no-lines')).toBeInTheDocument();
  });

  it('Given a deal not visible to the caller, when its detail 404s, then the panel says so', async () => {
    stubFetch(session(), dealsServer([deal('d1')]).route);
    renderAt('/deals/zzz');

    expect(await screen.findByRole('alert')).toHaveTextContent('This deal is not visible to you.');
  });
});

describe('Add line', () => {
  it('Given a deal detail, when a line is added, then the detail refetches and shows the new line from the server', async () => {
    const server = dealsServer([deal('d1', { lines: [line('l1', 'd1')] })]);
    const fetchMock = stubFetch(session(), server.route);
    renderAt('/deals/d1');

    const form = await screen.findByRole('form', { name: 'Add line' });
    await userEvent.type(within(form).getByLabelText('Product'), 'GASKET-2');
    await userEvent.clear(within(form).getByLabelText('Quantity'));
    await userEvent.type(within(form).getByLabelText('Quantity'), '5');
    await userEvent.type(within(form).getByLabelText('Unit price'), '7.5');
    await userEvent.type(within(form).getByLabelText('Price-list version'), 'PL-9');
    await userEvent.click(within(form).getByRole('button', { name: 'Add line' }));

    const added = await screen.findByTestId('line-n1');
    expect(added).toHaveTextContent('GASKET-2');
    expect(added).toHaveTextContent('37.50');
    expect(screen.getByTestId('line-l1')).toBeInTheDocument();
    expect(requests(fetchMock, 'POST', '/api/deals/d1/lines')).toHaveLength(1);
    expect(JSON.parse(String((requests(fetchMock, 'POST', '/api/deals/d1/lines')[0]![1] as RequestInit).body))).toEqual({
      productRef: 'GASKET-2',
      unitPrice: 7.5,
      quantity: 5,
      priceListVersion: 'PL-9',
    });
    // Read-your-writes: the detail was fetched again after the write, not patched locally.
    await waitFor(() => expect(requests(fetchMock, 'GET', '/api/deals/d1').length).toBeGreaterThanOrEqual(2));
    expect(within(form).getByLabelText('Product')).toHaveValue('');
  });

  it('Given the add-line form, when the button is clicked twice fast, then only one request is in flight (edge 10)', async () => {
    let release: (response: Response) => void = () => {};
    const server = dealsServer([deal('d1')]);
    const fetchMock = stubFetch(session(), (url, init) =>
      init?.method === 'POST'
        ? new Promise<Response>((resolve) => {
            release = resolve;
          })
        : server.route(url, init),
    );
    renderAt('/deals/d1');

    const form = await screen.findByRole('form', { name: 'Add line' });
    fireEvent.change(within(form).getByLabelText('Product'), { target: { value: 'X' } });
    fireEvent.change(within(form).getByLabelText('Unit price'), { target: { value: '1' } });
    const submit = within(form).getByRole('button', { name: 'Add line' });
    fireEvent.click(submit);
    fireEvent.click(submit);

    await waitFor(() => expect(within(form).getByRole('button', { name: 'Adding…' })).toBeDisabled());
    expect(requests(fetchMock, 'POST', '/api/deals/d1/lines')).toHaveLength(1);
    release(json(deal('d1')));
  });

  it.each([
    [400, { title: 'One or more validation errors occurred.', errors: { quantity: ['The quantity field is invalid.'] } }, 'The quantity field is invalid.'],
    [409, { error: 'The deal changed underneath you.' }, 'The deal changed underneath you.'],
    [422, { error: 'Lines cannot be added to a won deal.' }, 'Lines cannot be added to a won deal.'],
  ])('Given add-line returns %i, when rendered, then the server’s message is surfaced', async (status, body, message) => {
    const server = dealsServer([deal('d1')]);
    stubFetch(session(), (url, init) =>
      init?.method === 'POST' ? json(body, status) : server.route(url, init),
    );
    renderAt('/deals/d1');

    const form = await screen.findByRole('form', { name: 'Add line' });
    await userEvent.type(within(form).getByLabelText('Product'), 'X');
    await userEvent.type(within(form).getByLabelText('Unit price'), '1');
    await userEvent.click(within(form).getByRole('button', { name: 'Add line' }));

    expect(await within(form).findByTestId('add-line-failure')).toHaveTextContent(message);
    // The draft is kept so the user can correct and retry.
    expect(within(form).getByLabelText('Product')).toHaveValue('X');
  });

  it('Given the deal vanished from scope, when add-line returns an empty 404, then the panel says the deal is no longer visible', async () => {
    const server = dealsServer([deal('d1')]);
    stubFetch(session(), (url, init) =>
      init?.method === 'POST' ? new Response(null, { status: 404 }) : server.route(url, init),
    );
    renderAt('/deals/d1');

    const form = await screen.findByRole('form', { name: 'Add line' });
    await userEvent.type(within(form).getByLabelText('Product'), 'X');
    await userEvent.type(within(form).getByLabelText('Unit price'), '1');
    await userEvent.click(within(form).getByRole('button', { name: 'Add line' }));

    expect(await within(form).findByTestId('add-line-failure')).toHaveTextContent(
      'This deal is no longer visible to you.',
    );
  });
});

describe('Create deal', () => {
  async function openCreate() {
    await screen.findByRole('table', { name: 'Deals' });
    await userEvent.click(screen.getByRole('button', { name: 'New deal' }));
    return screen.getByRole('complementary', { name: 'New deal' });
  }

  it('Given a valid draft, when created, then POST /api/deals is sent once, the grid refetches and the new deal opens in the URL', async () => {
    const server = dealsServer([deal('d1')]);
    const fetchMock = stubFetch(session(), server.route);
    renderAt('/deals');

    const panel = await openCreate();
    await userEvent.type(within(panel).getByLabelText('Account ID'), ACCOUNT);
    await userEvent.type(within(panel).getByLabelText('Name'), 'Fleet renewal');
    await userEvent.type(within(panel).getByLabelText('Amount'), '9000');
    await userEvent.click(within(panel).getByRole('button', { name: 'Create deal' }));

    await screen.findByTestId('deal-row-d2');
    await waitFor(() => expect(window.location.pathname).toBe('/deals/d2'));
    const posts = requests(fetchMock, 'POST', '/api/deals');
    expect(posts).toHaveLength(1);
    expect(JSON.parse(String((posts[0]![1] as RequestInit).body))).toEqual({
      accountId: ACCOUNT,
      name: 'Fleet renewal',
      amount: 9000,
      discountPct: 0,
    });
    expect(screen.queryByRole('complementary', { name: 'New deal' })).not.toBeInTheDocument();
  });

  it('Given a create form, when the button is clicked twice fast, then only one request is in flight (edge 10)', async () => {
    let release: (response: Response) => void = () => {};
    const server = dealsServer([deal('d1')]);
    const fetchMock = stubFetch(session(), (url, init) =>
      init?.method === 'POST'
        ? new Promise<Response>((resolve) => {
            release = resolve;
          })
        : server.route(url, init),
    );
    renderAt('/deals');

    const panel = await openCreate();
    fireEvent.change(within(panel).getByLabelText('Account ID'), { target: { value: ACCOUNT } });
    fireEvent.change(within(panel).getByLabelText('Name'), { target: { value: 'Ada' } });
    fireEvent.change(within(panel).getByLabelText('Amount'), { target: { value: '1' } });
    const submit = within(panel).getByRole('button', { name: 'Create deal' });
    fireEvent.click(submit);
    fireEvent.click(submit);

    await waitFor(() => expect(within(panel).getByRole('button', { name: 'Creating…' })).toBeDisabled());
    expect(requests(fetchMock, 'POST', '/api/deals')).toHaveLength(1);
    release(json(deal('d9'), 201));
  });

  it('Given an out-of-scope account, when create returns 404, then the server’s message is surfaced and the draft kept', async () => {
    const server = dealsServer([deal('d1')]);
    stubFetch(session(), (url, init) =>
      init?.method === 'POST'
        ? json({ error: 'No account with this id is visible to you.' }, 404)
        : server.route(url, init),
    );
    renderAt('/deals');

    const panel = await openCreate();
    await userEvent.type(within(panel).getByLabelText('Account ID'), '99999999-9999-9999-9999-999999999999');
    await userEvent.type(within(panel).getByLabelText('Name'), 'Ada');
    await userEvent.type(within(panel).getByLabelText('Amount'), '1');
    await userEvent.click(within(panel).getByRole('button', { name: 'Create deal' }));

    expect(await within(panel).findByTestId('create-failure')).toHaveTextContent(
      'No account with this id is visible to you.',
    );
    expect(within(panel).getByLabelText('Name')).toHaveValue('Ada');
  });

  it('Given an invalid draft, when submitted, then problems are listed and no request is sent', async () => {
    const fetchMock = stubFetch(session(), dealsServer([deal('d1')]).route);
    renderAt('/deals');

    const panel = await openCreate();
    await userEvent.click(within(panel).getByRole('button', { name: 'Create deal' }));

    expect(within(panel).getByRole('alert')).toHaveTextContent('Name is required.');
    expect(requests(fetchMock, 'POST', '/api/deals')).toHaveLength(0);
  });
});

describe('Account names on deals', () => {
  it('Given accounts.read, when the grid and detail render, then the account column shows the account name', async () => {
    stubFetch(
      session({ permissions: [Permissions.DealsRead, Permissions.AccountsRead] }),
      dealsServer([deal('d1')], [account(ACCOUNT, 'Contoso Freight')]).route,
    );
    renderAt('/deals/d1');

    const row = await screen.findByTestId('deal-row-d1');
    await waitFor(() => expect(row).toHaveTextContent('Contoso Freight'));
    const detail = await screen.findByTestId('deal-detail');
    expect(detail).toHaveTextContent('Contoso Freight');
  });

  it('Given no accounts.read, when the grid renders, then the account falls back to the short id and no GET /api/accounts is issued', async () => {
    const fetchMock = stubFetch(session(), dealsServer([deal('d1')], [account(ACCOUNT, 'Contoso Freight')]).route);
    renderAt('/deals');

    const row = await screen.findByTestId('deal-row-d1');
    expect(row).toHaveTextContent(ACCOUNT.slice(0, 8));
    expect(row).not.toHaveTextContent('Contoso Freight');
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(requests(fetchMock, 'GET', '/api/accounts')).toHaveLength(0);
  });
});

describe('deal form model', () => {
  it('requires a GUID account, a name, a non-negative amount and a 0–100 discount', () => {
    const result = toCreateDeal({ ...EMPTY_DEAL_DRAFT, accountId: 'nope', amount: '-1', discountPct: '120' });
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.problems).toEqual([
        'Account must be an account id (a GUID).',
        'Name is required.',
        'Amount must be a non-negative number.',
        'Discount must be a percentage between 0 and 100.',
      ]);
    }
  });

  it('requires a product, a price and a positive whole quantity; a blank price-list version is null', () => {
    expect(toAddLine({ ...EMPTY_LINE_DRAFT, quantity: '0' }).ok).toBe(false);
    expect(toAddLine({ ...EMPTY_LINE_DRAFT, productRef: 'A', unitPrice: '2', quantity: '1.5' }).ok).toBe(false);
    expect(toAddLine({ productRef: ' A ', unitPrice: '2.5', quantity: '3', priceListVersion: '  ' })).toEqual({
      ok: true,
      request: { productRef: 'A', unitPrice: 2.5, quantity: 3, priceListVersion: null },
    });
  });

  it('reads a 404 as the account on create and as the deal on add-line', () => {
    expect(describeDealError(new ApiError(404, 'x', null), 'create').message).toBe(
      'No account with this id is visible to you.',
    );
    expect(describeDealError(new ApiError(404, 'x', null), 'add-line').message).toBe(
      'This deal is no longer visible to you.',
    );
    expect(describeDealError(new ApiError(422, 'x', { error: 'Nope.' }), 'add-line')).toEqual({
      kind: 'rejected',
      message: 'Nope.',
    });
  });
});
