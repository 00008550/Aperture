import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import App from '../App';
import type { AccountView, ContactView, DealView, Session } from '../api';
import { setAccessToken } from '../auth';
import { Permissions, type Permission } from '../permissions';

/**
 * 010-P8 — accessibility & motion hardening, asserted through the real `App` (router, guard,
 * hooks, screens) with only `fetch`, the canvas context and `matchMedia` stubbed. jsdom does not
 * lay out or compute colour, so contrast and narrow-width layout are browser-verified and measured
 * (see the P8 report); what jsdom *can* prove — focus order, disabled-ness, names, descriptions,
 * and whether the field ever starts a loop — is proven here.
 */

const TENANT = '11111111-1111-1111-1111-111111111111';
const USER = '22222222-2222-2222-2222-222222222222';
const ACCOUNT = '33333333-3333-3333-3333-333333333333';

const ALL_SALES: Permission[] = [
  Permissions.AccountsRead,
  Permissions.AccountsWrite,
  Permissions.ContactsRead,
  Permissions.ContactsWrite,
  Permissions.DealsRead,
  Permissions.DealsWrite,
  Permissions.DealsDiscountApprove,
];
const READ_ONLY: Permission[] = [Permissions.AccountsRead, Permissions.ContactsRead, Permissions.DealsRead];

const session = (permissions: Permission[]): Session => ({
  tenantId: TENANT,
  userId: USER,
  email: 'ada@northwind.example',
  displayName: 'Ada',
  permissions,
  scopes: [{ kind: 'Own', targetId: null }],
});

const account: AccountView = {
  id: ACCOUNT,
  tenantId: TENANT,
  ownerUserId: USER,
  name: 'Northwind Trading Company',
  taxId: 'TAX-1',
  creditLimit: 5000,
  paymentTermsDays: 30,
  regionId: null,
  teamId: null,
  createdAt: '2026-09-01T00:00:00Z',
  version: 1,
} as AccountView;

const contact: ContactView = {
  id: 'c1',
  tenantId: TENANT,
  accountId: ACCOUNT,
  accountName: null,
  ownerUserId: USER,
  teamId: null,
  regionId: null,
  name: 'Grace Hopper',
  email: 'grace@northwind.example',
  phone: null,
  messenger: null,
  isDeparted: false,
  departedAt: null,
  createdAt: '2026-09-01T00:00:00Z',
};

const deal = (over: Partial<DealView> = {}): DealView => ({
  id: 'd1',
  tenantId: TENANT,
  accountId: ACCOUNT,
  accountName: null,
  ownerUserId: USER,
  teamId: null,
  regionId: null,
  name: 'Q4 restock',
  stage: 'negotiation',
  amount: 1200,
  discountPct: 30,
  frozenPriceListVersion: 'PL-2026',
  pendingApproval: false,
  lostReasonCode: null,
  createdAt: '2026-09-01T00:00:00Z',
  version: 7,
  lines: [],
  ...over,
});

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

/** A read-only Sales server: every grid has one row, the one deal has the given shape. */
function stubServer(me: Session, theDeal: DealView = deal()) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://localhost');
      switch (url.pathname) {
        case '/api/me':
          return json(me);
        case '/api/accounts':
          return json({ items: [account], nextCursor: null });
        case `/api/accounts/${ACCOUNT}`:
          return json(account);
        case '/api/contacts':
          return json({ items: [contact], nextCursor: null });
        case '/api/deals':
          return json({ items: [{ ...theDeal, lines: [] }], nextCursor: null });
        case `/api/deals/${theDeal.id}`:
          return json(theDeal);
        default:
          return json(null, 404);
      }
    }),
  );
}

function stubMatchMedia(reducedMotion: boolean) {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({
      matches: query.includes('reduced-motion') ? reducedMotion : false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia,
  );
}

/** A 2D context that accepts every draw call, so the field runs its real path under jsdom. */
function stubCanvas() {
  const noop = () => {};
  const ctx = new Proxy(
    { globalAlpha: 1, fillStyle: '', strokeStyle: '', lineWidth: 1 },
    {
      get: (target, key) =>
        key in target
          ? target[key as keyof typeof target]
          : key === 'createLinearGradient'
            ? () => ({ addColorStop: noop })
            : noop,
      set: (target, key, value) => {
        (target as Record<string | symbol, unknown>)[key] = value;
        return true;
      },
    },
  );
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(ctx as never);
}

function renderAt(path: string, signedIn = true) {
  window.history.pushState({}, '', path);
  if (signedIn) setAccessToken('t-1');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return render(
    <QueryClientProvider client={client}>
      <App />
    </QueryClientProvider>,
  );
}

/**
 * Every element Tab reaches, in order, walking forward until focus wraps back to the start (or a
 * generous cap). This is the keyboard user's whole world: if a control is not in this list, it
 * cannot be reached without a mouse.
 */
async function tabStops(user: ReturnType<typeof userEvent.setup>): Promise<Element[]> {
  (document.activeElement as HTMLElement | null)?.blur();
  const stops: Element[] = [];
  for (let i = 0; i < 200; i++) {
    await user.tab();
    const el = document.activeElement;
    if (!el || el === document.body) break;
    if (stops.includes(el)) break;
    stops.push(el);
  }
  return stops;
}

/** Everything a pointer user could operate right now: enabled buttons, inputs, links, focusable rows. */
function operableControls(): Element[] {
  return [
    ...document.querySelectorAll(
      'a[href], button:not(:disabled), input:not(:disabled):not([type="hidden"]), textarea:not(:disabled), select:not(:disabled), [tabindex]:not([tabindex="-1"])',
    ),
  ];
}

const nameOf = (el: Element) =>
  el.getAttribute('aria-label') ?? el.textContent?.replace(/\s+/g, ' ').trim() ?? el.tagName;

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.history.pushState({}, '', '/');
});

// ---------------------------------------------------------------------------------------------

const ROUTES: { path: string; ready: () => Promise<unknown> }[] = [
  { path: '/', ready: () => screen.findByRole('heading', { name: 'Overview' }) },
  { path: '/accounts', ready: () => screen.findByRole('table', { name: 'Accounts' }) },
  { path: `/accounts/${ACCOUNT}`, ready: () => screen.findByRole('button', { name: 'Save changes' }) },
  { path: '/contacts', ready: () => screen.findByRole('table', { name: 'Contacts' }) },
  { path: '/deals', ready: () => screen.findByRole('table', { name: 'Deals' }) },
  { path: '/deals/d1', ready: () => screen.findByTestId('lifecycle') },
  { path: '/orders', ready: () => screen.findByTestId('route-locked') },
  { path: '/timeline', ready: () => screen.findByTestId('route-locked') },
  { path: '/administration', ready: () => screen.findByTestId('route-locked') },
];

describe('reduced motion holds on every route (edge 1, generalised)', () => {
  it.each(ROUTES.map((route) => [route.path, route] as const))(
    'Given prefers-reduced-motion, when %s renders, then the field is still and no rAF loop ever starts',
    async (_path, route) => {
      stubMatchMedia(true);
      stubCanvas();
      const raf = vi.spyOn(window, 'requestAnimationFrame');
      stubServer(session(ALL_SALES));
      renderAt(route.path);

      await route.ready();
      const field = document.querySelector('canvas.field')!;
      expect(field).toHaveAttribute('data-field-state', 'reduced');
      expect(raf).not.toHaveBeenCalled();
    },
  );

  it('Given prefers-reduced-motion, when the sign-in surface renders, then the field is still and no rAF loop starts', async () => {
    stubMatchMedia(true);
    stubCanvas();
    const raf = vi.spyOn(window, 'requestAnimationFrame');
    stubServer(session([]));
    renderAt('/', false);

    await screen.findByRole('heading', { name: 'Sign in' });
    expect(document.querySelector('canvas.field')).toHaveAttribute('data-field-state', 'reduced');
    expect(raf).not.toHaveBeenCalled();
  });
});

describe('the field is decoration only', () => {
  it('Given any signed-in route, when rendered, then the field is aria-hidden, not focusable, and never a Tab stop', async () => {
    stubMatchMedia(false);
    stubCanvas();
    stubServer(session(ALL_SALES));
    renderAt('/deals/d1');
    await screen.findByTestId('lifecycle');

    const field = document.querySelector('canvas.field')!;
    expect(field).toHaveAttribute('aria-hidden', 'true');
    expect(field).not.toHaveAttribute('tabindex');
    const stops = await tabStops(userEvent.setup());
    expect(stops.length).toBeGreaterThan(5);
    expect(stops).not.toContain(field);
  });
});

describe('keyboard reaches every control the user may use', () => {
  it.each([
    ['/accounts', ['New account']],
    [`/accounts/${ACCOUNT}`, ['New account', 'Save changes', 'Close panel']],
    ['/contacts', ['New contact', 'Depart Grace Hopper', 'Show departed']],
    ['/deals/d1', ['New deal', 'Won', 'Lost', 'Add line', 'Close panel']],
  ] as const)(
    'Given a user with every Sales permission, when %s renders, then Tab reaches every operable control, including %j',
    async (path, writes) => {
      stubMatchMedia(true);
      stubCanvas();
      stubServer(session(ALL_SALES));
      renderAt(path);
      await screen.findAllByRole('table');
      if (path.includes('/d1')) await screen.findByTestId('lifecycle');
      if (path.includes(ACCOUNT)) await screen.findByRole('button', { name: 'Save changes' });

      const stops = await tabStops(userEvent.setup());
      // Full traversal: nothing a mouse can operate is out of the keyboard's reach.
      const unreachable = operableControls().filter((el) => !stops.includes(el));
      expect(unreachable.map(nameOf)).toEqual([]);
      // And, by accessible name, the write controls this portion is about.
      for (const name of writes) {
        const control = screen.queryByRole('button', { name }) ?? screen.getByRole('switch', { name });
        expect(stops).toContain(control);
      }
    },
  );

  it('Given a held deal and a lead with deals.discount.approve, when the detail renders, then Tab reaches the approval reason and Approve discount', async () => {
    stubMatchMedia(true);
    stubCanvas();
    stubServer(session(ALL_SALES), deal({ pendingApproval: true }));
    renderAt('/deals/d1');
    await screen.findByTestId('held');

    const stops = await tabStops(userEvent.setup());
    expect(stops).toContain(screen.getByLabelText('Approval reason'));
    expect(stops).toContain(screen.getByRole('button', { name: 'Approve discount' }));
  });
});

describe('denied controls are visible, skipped by Tab, and say why (mirrors Navigation.tsx)', () => {
  it.each([
    ['/accounts', [['New account', `Requires ${Permissions.AccountsWrite}`]]],
    [
      '/contacts',
      [
        ['New contact', `Requires ${Permissions.ContactsWrite}`],
        ['Depart Grace Hopper', `Requires ${Permissions.ContactsWrite}`],
      ],
    ],
    [
      '/deals/d1',
      [
        ['New deal', `Requires ${Permissions.DealsWrite}`],
        ['Won', `Requires ${Permissions.DealsWrite}`],
        ['Lost', `Requires ${Permissions.DealsWrite}`],
        ['Add line', `Requires ${Permissions.DealsWrite}`],
      ],
    ],
  ] as const)(
    'Given a read-only user, when %s renders, then each write control keeps its action name, is disabled and not a Tab stop, and describes the missing permission',
    async (path, denied) => {
      stubMatchMedia(true);
      stubCanvas();
      stubServer(session(READ_ONLY));
      renderAt(path);
      await screen.findAllByRole('table');
      if (path.includes('/d1')) await screen.findByTestId('lifecycle');

      const stops = await tabStops(userEvent.setup());
      for (const [name, reason] of denied) {
        const control = screen.getByRole('button', { name });
        expect(control).toBeVisible();
        expect(control).toBeDisabled();
        expect(control).toHaveAccessibleName(name);
        expect(control).toHaveAccessibleDescription(reason);
        expect(stops).not.toContain(control);
      }
      // Nothing that carries a denial reason is reachable by Tab — nav items included.
      for (const el of document.querySelectorAll('[data-denied-reason]')) {
        expect(stops).not.toContain(el);
      }
      // …and Tab still works: the read-only user can reach the rows they may open.
      expect(stops.some((el) => el.tagName === 'TR')).toBe(true);
    },
  );

  it('Given a held deal and an agent without deals.discount.approve, when rendered, then Approve discount is named for its action, described by the permission, and skipped by Tab', async () => {
    stubMatchMedia(true);
    stubCanvas();
    stubServer(session(READ_ONLY.concat(Permissions.DealsWrite)), deal({ pendingApproval: true }));
    renderAt('/deals/d1');
    await screen.findByTestId('held');

    const approve = screen.getByRole('button', { name: 'Approve discount' });
    expect(approve).toHaveAccessibleDescription(`Requires ${Permissions.DealsDiscountApprove}`);
    const won = screen.getByRole('button', { name: 'Won' });
    expect(won).toHaveAccessibleDescription(/Held for lead approval/);
    const stops = await tabStops(userEvent.setup());
    expect(stops).not.toContain(approve);
    expect(stops).not.toContain(won);
    // The move that is still open stays reachable.
    expect(stops).toContain(screen.getByRole('button', { name: 'Lost' }));
  });

  it('Given a denied nav section, when rendered, then its name is the section label and its description the permission', async () => {
    stubMatchMedia(true);
    stubCanvas();
    stubServer(session(READ_ONLY));
    renderAt('/');
    await screen.findByRole('heading', { name: 'Overview' });

    const orders = screen.getByText('Orders').closest('a')!;
    expect(orders).toHaveAccessibleName('Orders');
    expect(orders).toHaveAccessibleDescription(`Requires ${Permissions.OrdersRead}`);
    expect(orders).not.toHaveAttribute('title');
  });
});

describe('screen-reader labels', () => {
  it.each([
    ['/accounts', 'Accounts', /opens its account/],
    ['/contacts', 'Contacts', /opens its contact/],
    ['/deals', 'Deals', /opens its deal/],
  ] as const)('Given %s, when the grid renders, then it is named %s and describes how to open a row', async (path, name, hint) => {
    stubMatchMedia(true);
    stubCanvas();
    stubServer(session(ALL_SALES));
    renderAt(path);

    const grid = await screen.findByRole('table', { name });
    expect(grid).toHaveAccessibleDescription(hint);
    // Column headers are real headers, so a screen reader announces them per cell.
    expect(within(grid).getAllByRole('columnheader').length).toBeGreaterThan(2);
    // A focused row is announced by the record it opens — Chrome computes no name for a bare row.
    const focusable = within(grid)
      .getAllByRole('row')
      .filter((row) => row.getAttribute('tabindex') === '0');
    expect(focusable.length).toBeGreaterThan(0);
    for (const row of focusable) expect(row).toHaveAccessibleName(rowName[name]);
  });

  const rowName = {
    Accounts: 'Northwind Trading Company',
    Contacts: 'Grace Hopper',
    Deals: 'Q4 restock, negotiation',
  } as const;

  it('Given an open deal, when the lifecycle renders, then its moves are a group named "Move to stage" and described by the current stage', async () => {
    stubMatchMedia(true);
    stubCanvas();
    stubServer(session(ALL_SALES));
    renderAt('/deals/d1');

    const section = await screen.findByTestId('lifecycle');
    expect(section).toHaveAccessibleName('Lifecycle');
    const group = within(section).getByRole('group', { name: 'Move to stage' });
    expect(group).toHaveAccessibleDescription(/From Negotiation to/);
  });

  it('Given the dev picker, when it lists demo users, then each button is named "Sign in as <name>" and described by what it demonstrates and the email', async () => {
    stubMatchMedia(true);
    stubCanvas();
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        json({
          tenantId: TENANT,
          tenantSlug: 'northwind-demo',
          tenantName: 'Northwind Supply',
          users: [
            { userId: 'u-ada', email: 'ada@northwind.example', displayName: 'Ada Admin', demonstrates: 'Admin' },
            { userId: 'u-vera', email: 'vera@northwind.example', displayName: 'Vera Viewer', demonstrates: 'East region, read-only' },
          ],
        }),
      ),
    );
    renderAt('/', false);

    const ada = await screen.findByRole('button', { name: 'Sign in as Ada Admin' });
    expect(ada).toHaveAccessibleDescription('Admin ada@northwind.example');
    const vera = screen.getByRole('button', { name: 'Sign in as Vera Viewer' });
    expect(vera).toHaveAccessibleDescription('East region, read-only vera@northwind.example');
    const stops = await tabStops(userEvent.setup());
    expect(stops).toContain(ada);
    expect(stops).toContain(vera);
  });
});

describe('styles.css motion guard', () => {
  const css = Object.values(
    import.meta.glob<string>('../styles.css', { query: '?raw', import: 'default', eager: true }),
  )[0]!;

  it('turns every animation and transition off under prefers-reduced-motion, for screens that exist and screens not yet written', () => {
    const block = /@media \(prefers-reduced-motion: reduce\) \{([\s\S]*?)\n\}/g;
    const bodies = [...css.matchAll(block)].map((m) => m[1]!).join('\n');
    expect(bodies).toMatch(/\*,\s*\*::before,\s*\*::after\s*\{[^}]*animation:\s*none !important/);
    expect(bodies).toMatch(/\*,\s*\*::before,\s*\*::after\s*\{[^}]*transition:\s*none !important/);
  });

  it('never sizes the fixed field in viewport width units (100vw includes the scrollbar and scrolls the page sideways)', () => {
    const fieldRule = /canvas\.field\s*\{([^}]*)\}/.exec(css)![1]!;
    expect(fieldRule).not.toMatch(/100vw/);
  });

  it('lets the content column shrink, so a wide grid scrolls in its card rather than the page', () => {
    expect(css).toMatch(/\.shell\s*\{[^}]*grid-template-columns:\s*244px minmax\(0, 1fr\)/);
    expect(css).toMatch(/\.data-grid-card\s*\{[^}]*overflow-x:\s*auto/);
  });
});
