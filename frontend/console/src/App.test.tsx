import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import App from './App';
import type { Session } from './api';
import { getAccessToken, setAccessToken } from './auth';
import { Permissions } from './permissions';

function renderApp() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });

  return render(
    <QueryClientProvider client={client}>
      <App />
    </QueryClientProvider>,
  );
}

const session = (over: Partial<Session> = {}): Session => ({
  tenantId: '11111111-1111-1111-1111-111111111111',
  userId: '22222222-2222-2222-2222-222222222222',
  email: 'lead@northwind.example',
  displayName: 'Regional Lead',
  permissions: [Permissions.DealsRead],
  scopes: [{ kind: 'Region', targetId: '33333333-3333-3333-3333-333333333333' }],
  ...over,
});

/** Answers /api/me with a body, or with a status when given a number. */
function stubMe(answer: Session | number) {
  const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => {
    if (typeof answer === 'number') {
      return new Response(null, { status: answer });
    }
    return new Response(JSON.stringify(answer), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  });

  vi.stubGlobal('fetch', fetchMock as unknown as typeof fetch);
  return fetchMock;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the console session', () => {
  it('shows sign-in and no navigation when there is no token', () => {
    stubMe(session());
    renderApp();

    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByRole('navigation')).not.toBeInTheDocument();
  });

  it('signs in with a token and renders the session from GET /api/me', async () => {
    const fetchMock = stubMe(session());
    renderApp();

    await userEvent.type(screen.getByLabelText('Access token'), 'a-token');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText('Regional Lead')).toBeInTheDocument();
    expect(screen.getByText('lead@northwind.example')).toBeInTheDocument();

    const [, init] = fetchMock.mock.calls[0]!;
    expect((init?.headers as Record<string, string>).authorization).toBe('Bearer a-token');
  });

  it('gates navigation on the permissions the API returned, not on anything local', async () => {
    stubMe(session({ permissions: [Permissions.DealsRead] }));
    setAccessToken('a-token');
    renderApp();

    // Everything is denied until /api/me answers — the gate opens on the response, never
    // optimistically, so waitFor is asserting the transition and not just the end state.
    expect(screen.getByText('Deals').closest('a')).toHaveAttribute('aria-disabled', 'true');

    await waitFor(() =>
      expect(screen.getByText('Deals').closest('a')).toHaveAttribute('href', '#deals'),
    );
    expect(screen.getByText('Orders').closest('a')).toHaveAttribute('aria-disabled', 'true');
  });

  it('states plainly that a user with no scopes sees nothing', async () => {
    // DOMAIN.md §5.1: "no scopes" must never read as "no data yet" or as "unfiltered".
    stubMe(session({ scopes: [] }));
    setAccessToken('a-token');
    renderApp();

    const scopes = await screen.findByTestId('scopes');
    expect(scopes).toHaveTextContent('No data scopes granted.');
    expect(scopes).toHaveTextContent('nothing is visible to you at all');
  });

  it('discards a token the API refuses and returns to sign-in', async () => {
    stubMe(401);
    setAccessToken('a-stale-token');
    renderApp();

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('That token was refused');
    await waitFor(() => expect(getAccessToken()).toBeNull());
  });

  it('signs out by dropping the token, not by hiding the shell', async () => {
    stubMe(session());
    setAccessToken('a-token');
    renderApp();

    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }));

    expect(getAccessToken()).toBeNull();
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });
});
