import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import App from '../../../App';
import { ApiError, type DealView, type Session } from '../../../api';
import { getAccessToken, setAccessToken } from '../../../auth';
import { Permissions } from '../../../permissions';
import {
  describeLifecycleError,
  nextStages,
  offeredMoves,
  toApproval,
  toTransition,
} from './lifecycleModel';

/**
 * P7 — the deal lifecycle control, exercised through the real `App` (router + guard + hooks) with
 * only `fetch` stubbed. The stub is a small stateful deals server that mirrors
 * `DealStateMachine.cs` (legal edges, the rule guards, the discount hold) unless a test overrides
 * a write to answer something specific (a 409, a 403, a 400).
 */

const TENANT = '11111111-1111-1111-1111-111111111111';
const USER = '22222222-2222-2222-2222-222222222222';
const ACCOUNT = '33333333-3333-3333-3333-333333333333';
const THRESHOLD = 10;

const session = (over: Partial<Session> = {}): Session => ({
  tenantId: TENANT,
  userId: USER,
  email: 'agent@northwind.example',
  displayName: 'Agent',
  permissions: [Permissions.DealsRead, Permissions.DealsWrite],
  scopes: [{ kind: 'Own', targetId: null }],
  ...over,
});

const lead = () =>
  session({
    permissions: [Permissions.DealsRead, Permissions.DealsWrite, Permissions.DealsDiscountApprove],
  });

const pricedLine = (dealId: string) => ({
  id: 'l1',
  dealId,
  productRef: 'SKU-1',
  unitPrice: 100,
  quantity: 2,
  priceListVersion: 'PL-2026',
});

const deal = (over: Partial<DealView> = {}): DealView => ({
  id: 'd1',
  tenantId: TENANT,
  accountId: ACCOUNT,
  ownerUserId: USER,
  teamId: null,
  regionId: null,
  name: 'Harbor refit',
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

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

type Body = Record<string, unknown>;
type Override = (body: Body, row: DealView) => Response | Promise<Response> | undefined;

const EDGES: Record<string, string[]> = {
  new: ['qualified'],
  qualified: ['quoted'],
  quoted: ['negotiation'],
  negotiation: ['won', 'lost'],
};

/** A one-deal server mirroring the real state machine; `override` may answer a write first. */
function lifecycleServer(
  initial: DealView,
  overrides: { transition?: Override; approve?: Override } = {},
) {
  const row: DealView & { approved?: boolean } = { ...initial, lines: [...initial.lines] };
  const bump = () => {
    row.version += 1;
  };
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    const method = init?.method ?? 'GET';
    if (url.pathname === '/api/me') return json(currentSession);
    if (method === 'GET' && url.pathname === '/api/deals') {
      return json({ items: [{ ...row, lines: [] }], nextCursor: null });
    }
    if (method === 'GET' && url.pathname === `/api/deals/${row.id}`) return json({ ...row });
    const body = init?.body ? (JSON.parse(String(init.body)) as Body) : {};
    if (method === 'POST' && url.pathname === `/api/deals/${row.id}/transition`) {
      const forced = await overrides.transition?.(body, row);
      if (forced) return forced;
      if (body.expectedVersion !== row.version) return json({ ...row }, 409);
      const to = String(body.targetStage);
      if (!(EDGES[row.stage] ?? []).includes(to)) {
        return json({ error: `Cannot move a deal from ${row.stage} to ${to}.` }, 422);
      }
      if (to === 'won') {
        if (!row.lines.some((l) => l.unitPrice > 0 && l.quantity > 0)) {
          return json(
            { error: 'A deal can be won only with at least one line that has a price and a quantity.' },
            422,
          );
        }
        if (row.discountPct > THRESHOLD && !row.approved) {
          row.pendingApproval = true;
          bump();
          return json({ ...row });
        }
      }
      if (to === 'lost' && !String(body.reason ?? '').trim()) {
        return json({ error: 'A deal can be lost only with a reason code.' }, 422);
      }
      if (to === 'quoted') row.frozenPriceListVersion = String(body.priceListVersion);
      if (to === 'lost') row.lostReasonCode = String(body.reason);
      row.stage = to;
      bump();
      return json({ ...row });
    }
    if (method === 'POST' && url.pathname === `/api/deals/${row.id}/approve-discount`) {
      const forced = await overrides.approve?.(body, row);
      if (forced) return forced;
      if (!String(body.reason ?? '').trim()) {
        return json({ error: 'A discount approval requires a reason.' }, 400);
      }
      if (!row.pendingApproval) return json({ error: 'This deal has no pending discount approval.' }, 409);
      if (body.expectedVersion !== row.version) return json({ ...row }, 409);
      row.pendingApproval = false;
      row.approved = true;
      bump();
      return json({ ...row });
    }
    return json({}, 404);
  });
  let currentSession = session();
  vi.stubGlobal('fetch', fetchMock);
  return {
    row,
    fetchMock,
    as: (next: Session) => {
      currentSession = next;
    },
  };
}

const posts = (fetchMock: ReturnType<typeof vi.fn>, suffix: string) =>
  fetchMock.mock.calls
    .filter(([input, init]) => {
      const url = new URL(String(input), 'http://localhost');
      return (init as RequestInit | undefined)?.method === 'POST' && url.pathname.endsWith(suffix);
    })
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)) as Body);

function renderDeal(id = 'd1') {
  window.history.pushState({}, '', `/deals/${id}`);
  setAccessToken('t-1');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  render(
    <QueryClientProvider client={client}>
      <App />
    </QueryClientProvider>,
  );
}

const lifecycle = () => screen.findByTestId('lifecycle');
const detailChip = async () =>
  within(await screen.findByTestId('deal-detail')).getByTestId('stage-chip');
const moveButtons = (section: HTMLElement) =>
  within(section)
    .queryAllByRole('button')
    .filter((button) => button.hasAttribute('data-target'))
    .map((button) => button.getAttribute('data-target'));

afterEach(() => {
  vi.unstubAllGlobals();
  window.history.pushState({}, '', '/');
});

describe('Deal lifecycle — offered moves', () => {
  it.each([
    ['new', ['qualified']],
    ['qualified', ['quoted']],
    ['quoted', ['negotiation']],
    ['negotiation', ['won', 'lost']],
  ])('Given a %s deal, when the detail renders, then only the legal next stages are offered', async (stage, expected) => {
    lifecycleServer(deal({ stage }));
    renderDeal();

    const section = await lifecycle();
    await waitFor(() => expect(moveButtons(section)).toEqual(expected));
  });

  it('Given a deal detail, when the lifecycle renders beside the add-line form, then React reports no duplicate keys', async () => {
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    lifecycleServer(deal({ stage: 'negotiation' }));
    renderDeal();

    await screen.findByRole('button', { name: 'Won' });
    const keyWarnings = errors.mock.calls.filter((args) => String(args[0]).includes('same key'));
    errors.mockRestore();
    expect(keyWarnings).toEqual([]);
  });

  it.each(['won', 'lost'])(
    'Given a terminal %s deal, when the detail renders, then no transition controls are shown',
    async (stage) => {
      lifecycleServer(deal({ stage, lostReasonCode: stage === 'lost' ? 'price' : null }));
      renderDeal();

      const section = await lifecycle();
      expect(section).toHaveAttribute('data-terminal', 'true');
      expect(within(section).queryAllByRole('button')).toHaveLength(0);
      expect(within(section).getByTestId('terminal')).toHaveTextContent('no further moves');
    },
  );

  it('Given a user without deals.write, when the detail renders, then every move is disabled — not hidden', async () => {
    const server = lifecycleServer(deal({ stage: 'negotiation' }));
    server.as(session({ permissions: [Permissions.DealsRead] }));
    renderDeal();

    const section = await lifecycle();
    const won = await within(section).findByRole('button', { name: 'Won' });
    expect(won).toBeDisabled();
    expect(won).toHaveAttribute('title', 'Requires deals.write');
    expect(within(section).getByRole('button', { name: 'Lost' })).toBeDisabled();
  });
});

describe('Deal lifecycle — the server is the authority', () => {
  it('Given a new deal, when it is moved to qualified, then the request carries expectedVersion and the server-returned stage is rendered', async () => {
    const server = lifecycleServer(deal({ stage: 'new', version: 7 }));
    renderDeal();

    const section = await lifecycle();
    await userEvent.click(await within(section).findByRole('button', { name: 'Qualified' }));

    await waitFor(async () => expect(await detailChip()).toHaveAttribute('data-stage', 'qualified'));
    expect(posts(server.fetchMock, '/transition')).toEqual([
      { targetStage: 'qualified', reason: null, priceListVersion: null, expectedVersion: 7 },
    ]);
    expect(screen.getByTestId('lifecycle-done')).toHaveTextContent('Moved to Qualified.');
  });

  it('Given a move is in flight, when the server has not answered, then the stage is not advanced client-side', async () => {
    let release: (value: Response) => void = () => {};
    const server = lifecycleServer(deal({ stage: 'new' }), {
      transition: () => new Promise<Response>((resolve) => (release = resolve)),
    });
    renderDeal();

    const section = await lifecycle();
    await userEvent.click(await within(section).findByRole('button', { name: 'Qualified' }));

    expect(await within(section).findByRole('button', { name: 'Moving…' })).toBeDisabled();
    expect(await detailChip()).toHaveAttribute('data-stage', 'new');
    Object.assign(server.row, { stage: 'qualified', version: 8 });
    release(json({ ...server.row }));
    await waitFor(async () => expect(await detailChip()).toHaveAttribute('data-stage', 'qualified'));
  });

  it('Given a qualified deal, when quoted is chosen with no price-list version, then it is blocked client-side; with one, it is sent', async () => {
    const server = lifecycleServer(deal({ stage: 'qualified' }));
    renderDeal();

    const section = await lifecycle();
    await userEvent.click(await within(section).findByRole('button', { name: 'Quoted' }));
    await userEvent.click(within(section).getByRole('button', { name: 'Move to Quoted' }));
    expect(within(section).getByRole('alert')).toHaveTextContent('price-list version');
    expect(posts(server.fetchMock, '/transition')).toHaveLength(0);

    await userEvent.type(within(section).getByLabelText('Price-list version to freeze'), 'PL-2026');
    await userEvent.click(within(section).getByRole('button', { name: 'Move to Quoted' }));
    await waitFor(async () => expect(await detailChip()).toHaveAttribute('data-stage', 'quoted'));
    expect(posts(server.fetchMock, '/transition')[0]).toMatchObject({ priceListVersion: 'PL-2026' });
  });

  it('Given a negotiation deal, when lost is chosen without a reason code, then it is blocked client-side and nothing is sent', async () => {
    const server = lifecycleServer(deal({ stage: 'negotiation' }));
    renderDeal();

    const section = await lifecycle();
    await userEvent.click(await within(section).findByRole('button', { name: 'Lost' }));
    await userEvent.type(within(section).getByLabelText('Lost reason code'), '   ');
    await userEvent.click(within(section).getByRole('button', { name: 'Move to Lost' }));

    expect(within(section).getByRole('alert')).toHaveTextContent('A lost deal needs a reason code.');
    expect(posts(server.fetchMock, '/transition')).toHaveLength(0);
  });

  it('Given a negotiation deal, when lost is sent with a reason code, then the lost deal and its reason are rendered', async () => {
    const server = lifecycleServer(deal({ stage: 'negotiation' }));
    renderDeal();

    const section = await lifecycle();
    await userEvent.click(await within(section).findByRole('button', { name: 'Lost' }));
    await userEvent.type(within(section).getByLabelText('Lost reason code'), 'competitor');
    await userEvent.click(within(section).getByRole('button', { name: 'Move to Lost' }));

    const terminal = await screen.findByTestId('terminal');
    expect(terminal).toHaveTextContent('competitor');
    expect(posts(server.fetchMock, '/transition')[0]).toMatchObject({
      targetStage: 'lost',
      reason: 'competitor',
    });
  });

  it('Given the server refuses a lost move (422), when it answers, then its message is surfaced', async () => {
    lifecycleServer(deal({ stage: 'negotiation' }), {
      transition: () => json({ error: 'A deal can be lost only with a reason code.' }, 422),
    });
    renderDeal();

    const section = await lifecycle();
    await userEvent.click(await within(section).findByRole('button', { name: 'Lost' }));
    await userEvent.type(within(section).getByLabelText('Lost reason code'), 'price');
    await userEvent.click(within(section).getByRole('button', { name: 'Move to Lost' }));

    expect(await screen.findByTestId('lifecycle-failure')).toHaveTextContent(
      'A deal can be lost only with a reason code.',
    );
  });

  it('Given an illegal edge or a failed guard, when the server 422s, then the move is shown rejected with the server message and the stage is unchanged (edge 9)', async () => {
    lifecycleServer(deal({ stage: 'negotiation', lines: [] }));
    renderDeal();

    const section = await lifecycle();
    await userEvent.click(await within(section).findByRole('button', { name: 'Won' }));

    const failure = await screen.findByTestId('lifecycle-failure');
    expect(failure).toHaveTextContent('Move to Won rejected.');
    expect(failure).toHaveTextContent(
      'A deal can be won only with at least one line that has a price and a quantity.',
    );
    expect(await detailChip()).toHaveAttribute('data-stage', 'negotiation');
  });
});

describe('Deal lifecycle — discount hold and approval (edge 8)', () => {
  const overThreshold = () =>
    deal({ stage: 'negotiation', discountPct: 25, lines: [pricedLine('d1')] });

  it('Given an over-threshold discount, when won is attempted, then the 200 + pendingApproval renders a held state (not success, not error) and won is blocked', async () => {
    lifecycleServer(overThreshold());
    renderDeal();

    const section = await lifecycle();
    await userEvent.click(await within(section).findByRole('button', { name: 'Won' }));

    const held = await screen.findByTestId('held');
    expect(held).toHaveTextContent('Held for lead approval.');
    expect(screen.queryByTestId('lifecycle-done')).not.toBeInTheDocument();
    expect(screen.queryByTestId('lifecycle-failure')).not.toBeInTheDocument();
    expect(await detailChip()).toHaveAttribute('data-stage', 'negotiation');
    const won = within(section).getByRole('button', { name: 'Won' });
    expect(won).toBeDisabled();
    expect(won.getAttribute('title')).toMatch(/Held for lead approval/);
  });

  it('Given a held deal and an agent without deals.discount.approve, when rendered, then Approve discount is disabled — not hidden', async () => {
    lifecycleServer({ ...overThreshold(), pendingApproval: true });
    renderDeal();

    const approve = await screen.findByRole('button', { name: 'Approve discount' });
    expect(approve).toBeDisabled();
    expect(approve).toHaveAttribute('title', `Requires ${Permissions.DealsDiscountApprove}`);
    expect(screen.getByLabelText('Approval reason')).toBeDisabled();
  });

  it('Given a held deal and a lead, when approve is sent without a reason, then it is blocked client-side and nothing is sent', async () => {
    const server = lifecycleServer({ ...overThreshold(), pendingApproval: true });
    server.as(lead());
    renderDeal();

    const approve = await screen.findByRole('button', { name: 'Approve discount' });
    await waitFor(() => expect(approve).toBeEnabled());
    await userEvent.click(approve);

    expect(screen.getByRole('alert')).toHaveTextContent('An approval needs a reason');
    expect(posts(server.fetchMock, '/approve-discount')).toHaveLength(0);
  });

  it('Given a held deal and a lead, when the server answers approve with 400, then its message is surfaced', async () => {
    const server = lifecycleServer({ ...overThreshold(), pendingApproval: true }, {
      approve: () => json({ error: 'A discount approval requires a reason.' }, 400),
    });
    server.as(lead());
    renderDeal();

    const reason = await screen.findByLabelText('Approval reason');
    await waitFor(() => expect(reason).toBeEnabled());
    await userEvent.type(reason, 'strategic account');
    await userEvent.click(screen.getByRole('button', { name: 'Approve discount' }));

    expect(await screen.findByTestId('lifecycle-failure')).toHaveTextContent(
      'A discount approval requires a reason.',
    );
  });

  it('Given a held deal and a lead, when approved with a reason, then the hold clears and the deal can be won', async () => {
    const server = lifecycleServer({ ...overThreshold(), pendingApproval: true, version: 9 });
    server.as(lead());
    renderDeal();

    const reason = await screen.findByLabelText('Approval reason');
    await waitFor(() => expect(reason).toBeEnabled());
    await userEvent.type(reason, 'strategic account');
    await userEvent.click(screen.getByRole('button', { name: 'Approve discount' }));

    await waitFor(() => expect(screen.queryByTestId('held')).not.toBeInTheDocument());
    expect(posts(server.fetchMock, '/approve-discount')).toEqual([
      { reason: 'strategic account', expectedVersion: 9 },
    ]);
    const section = await lifecycle();
    await userEvent.click(within(section).getByRole('button', { name: 'Won' }));
    await waitFor(async () => expect(await detailChip()).toHaveAttribute('data-stage', 'won'));
  });

  it('Given a server 403 on approve, when it answers, then "not permitted" is shown and the user stays signed in', async () => {
    const server = lifecycleServer({ ...overThreshold(), pendingApproval: true }, {
      approve: () => new Response(null, { status: 403 }),
    });
    server.as(lead());
    renderDeal();

    const reason = await screen.findByLabelText('Approval reason');
    await waitFor(() => expect(reason).toBeEnabled());
    await userEvent.type(reason, 'ok');
    await userEvent.click(screen.getByRole('button', { name: 'Approve discount' }));

    expect(await screen.findByTestId('lifecycle-failure')).toHaveTextContent(
      'You are not permitted to do this.',
    );
    expect(getAccessToken()).toBe('t-1');
    expect(screen.getByTestId('deal-detail')).toBeInTheDocument();
  });
});

describe('Deal lifecycle — concurrency and double-submit', () => {
  it('Given a stale version, when the transition 409s, then the refetched deal is shown with explicit re-apply / discard, and nothing is resent until chosen', async () => {
    const server = lifecycleServer(deal({ stage: 'quoted', version: 7 }));
    renderDeal();
    const section = await lifecycle();
    await within(section).findByRole('button', { name: 'Negotiation' });
    // Someone else touches the deal (e.g. adds a line) after this tab read it.
    server.row.version = 8;

    await userEvent.click(within(section).getByRole('button', { name: 'Negotiation' }));

    const conflict = await screen.findByTestId('lifecycle-conflict');
    expect(conflict).toHaveTextContent('Changed by someone else.');
    expect(conflict).toHaveTextContent('version 8');
    expect(conflict).toHaveTextContent('Your move to Negotiation can still be applied.');
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(posts(server.fetchMock, '/transition')).toHaveLength(1);
    expect(within(section).getByRole('button', { name: 'Negotiation' })).toBeDisabled();

    await userEvent.click(
      within(conflict).getByRole('button', { name: 'Re-apply: move to Negotiation' }),
    );
    await waitFor(async () => expect(await detailChip()).toHaveAttribute('data-stage', 'negotiation'));
    expect(posts(server.fetchMock, '/transition').map((body) => body.expectedVersion)).toEqual([7, 8]);
  });

  it('Given a 409 whose current deal has moved past the attempt, when shown, then re-apply is not offered and discard clears it', async () => {
    lifecycleServer(deal({ stage: 'quoted' }), {
      // Someone else already moved it on: the server's current deal is past the attempted edge.
      transition: (_, row) => json(Object.assign(row, { stage: 'negotiation', version: 12 }), 409),
    });
    renderDeal();
    const section = await lifecycle();
    await userEvent.click(await within(section).findByRole('button', { name: 'Negotiation' }));

    const conflict = await screen.findByTestId('lifecycle-conflict');
    expect(conflict).toHaveTextContent('no longer applies');
    expect(within(conflict).queryByRole('button', { name: /Re-apply/ })).not.toBeInTheDocument();
    expect(await detailChip()).toHaveAttribute('data-stage', 'negotiation');

    await userEvent.click(within(conflict).getByRole('button', { name: 'Discard mine, keep theirs' }));
    expect(screen.queryByTestId('lifecycle-conflict')).not.toBeInTheDocument();
  });

  it('Given a move button, when it is clicked twice in one frame, then only one request is sent (edge 10)', async () => {
    const server = lifecycleServer(deal({ stage: 'new' }));
    renderDeal();
    const section = await lifecycle();
    const button = await within(section).findByRole('button', { name: 'Qualified' });

    fireEvent.click(button);
    fireEvent.click(button);

    await waitFor(async () => expect(await detailChip()).toHaveAttribute('data-stage', 'qualified'));
    expect(posts(server.fetchMock, '/transition')).toHaveLength(1);
  });
});

describe('lifecycleModel', () => {
  it('Given an unknown or terminal stage, when next stages are asked, then none are offered — fail closed', () => {
    expect(nextStages('archived')).toEqual([]);
    expect(nextStages('won')).toEqual([]);
    expect(nextStages('lost')).toEqual([]);
  });

  it('Given a held deal, when moves are offered, then won is blocked and lost is not', () => {
    const moves = offeredMoves(deal({ stage: 'negotiation', pendingApproval: true }), true);
    expect(moves.find((move) => move.target === 'won')?.blockedBy).toMatch(/Held for lead approval/);
    expect(moves.find((move) => move.target === 'lost')?.blockedBy).toBeNull();
  });

  it('builds requests that always carry the expected version and only the fields the edge reads', () => {
    expect(toTransition('lost', 4, { reason: ' price ', priceListVersion: 'x' })).toEqual({
      ok: true,
      request: { targetStage: 'lost', reason: 'price', priceListVersion: null, expectedVersion: 4 },
    });
    expect(toApproval('  ', 4)).toMatchObject({ ok: false });
    expect(toApproval(' why ', 4)).toEqual({ ok: true, request: { reason: 'why', expectedVersion: 4 } });
  });

  it('classifies failures: a 409 with a deal is a conflict, a 409 without one is a refusal, 403 is not-permitted', () => {
    expect(describeLifecycleError(new ApiError(409, 'x', deal())).kind).toBe('conflict');
    expect(
      describeLifecycleError(new ApiError(409, 'x', { error: 'This deal has no pending discount approval.' })),
    ).toEqual({ kind: 'rejected', message: 'This deal has no pending discount approval.' });
    expect(describeLifecycleError(new ApiError(403, 'x')).kind).toBe('denied');
    expect(describeLifecycleError(new TypeError('offline')).kind).toBe('failed');
  });
});
