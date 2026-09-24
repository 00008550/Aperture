import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SignIn } from '../SignIn';
import { getAccessToken } from '../auth';
import type { DevUsers } from './DevSignInPicker';

const demo: DevUsers = {
  tenantId: '0d000000-0000-4000-8000-000000000001',
  tenantSlug: 'northwind-demo',
  tenantName: 'Northwind Supply',
  users: [
    { userId: 'u-ada', email: 'ada@northwind.example', displayName: 'Ada Admin', demonstrates: 'Admin' },
    { userId: 'u-vera', email: 'vera@northwind.example', displayName: 'Vera Viewer', demonstrates: 'East region, read-only' },
  ],
};

function renderSignIn() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return render(
    <QueryClientProvider client={client}>
      <SignIn />
    </QueryClientProvider>,
  );
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe('the development sign-in picker', () => {
  it('renders in a dev build, below the paste form', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(demo)));
    renderSignIn();

    expect(await screen.findByRole('button', { name: /Sign in as Ada Admin/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Sign in as Vera Viewer/ })).toBeInTheDocument();
    // Paste stays the primary form.
    expect(screen.getByLabelText('Access token')).toBeInTheDocument();
  });

  it('is absent, and asks the API nothing, when the build is not a dev build', async () => {
    vi.stubEnv('DEV', false);
    const fetchMock = vi.fn(async () => json(demo));
    vi.stubGlobal('fetch', fetchMock);
    renderSignIn();

    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    // Give a lazily-loaded picker every chance to appear before asserting it did not.
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(screen.queryByText(/Development sign-in/)).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('stores the minted token through setAccessToken when a user is picked', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) =>
      String(input) === '/api/dev/token'
        ? json({ accessToken: 'minted.jwt.value', expiresAt: '2026-09-24T18:00:00Z' })
        : json(demo),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderSignIn();

    await userEvent.click(await screen.findByRole('button', { name: /Sign in as Vera Viewer/ }));

    await waitFor(() => expect(getAccessToken()).toBe('minted.jwt.value'));
    const mintCall = fetchMock.mock.calls.find(([input]) => String(input) === '/api/dev/token');
    expect(mintCall?.[1]?.method).toBe('POST');
    expect(JSON.parse(String(mintCall?.[1]?.body))).toEqual({ tenantId: demo.tenantId, userId: 'u-vera' });
  });

  it('says so inline when the dev endpoint is unreachable, and paste still works', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => {
      throw new TypeError('Failed to fetch');
    }));
    renderSignIn();

    expect(await screen.findByRole('alert')).toHaveTextContent(/unreachable/);

    await userEvent.type(screen.getByLabelText('Access token'), 'pasted.token');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(getAccessToken()).toBe('pasted.token');
  });

  it('points at the seed when the API has no demo tenant', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 404 })));
    renderSignIn();

    expect(await screen.findByRole('alert')).toHaveTextContent(/--seed-demo/);
  });
});
