import { describe, expect, it } from 'vitest';
import { render as rtlRender, screen } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router';
import { Navigation } from './Navigation';
import { Permissions, type Permission } from './permissions';

// NavLink needs a router; a MemoryRouter at / stands in for the app's BrowserRouter.
const render = (ui: ReactElement) => rtlRender(<MemoryRouter>{ui}</MemoryRouter>);

const holding = (...held: Permission[]) => (p: Permission) => held.includes(p);

describe('the permission gate', () => {
  it('enables an item whose permission the user holds', () => {
    render(<Navigation can={holding(Permissions.DealsRead)} />);

    const deals = screen.getByRole('link', { name: /^Deals$/ });
    expect(deals).toHaveAttribute('href', '/deals');
    expect(deals).not.toHaveAttribute('aria-disabled');
  });

  it('disables an item whose permission the user lacks', () => {
    render(<Navigation can={holding(Permissions.DealsRead)} />);

    const orders = screen.getByText('Orders').closest('a');
    expect(orders).toHaveAttribute('aria-disabled', 'true');
    // No href: not a link, so it cannot be followed by keyboard or middle click either.
    expect(orders).not.toHaveAttribute('href');
    expect(orders).toHaveAccessibleDescription(`Requires ${Permissions.OrdersRead}`);
  });

  it('disables every item for a user with no permissions at all', () => {
    render(<Navigation can={() => false} />);

    // Overview is the only followable link; every gated item is a disabled link (role="link",
    // aria-disabled, no href — 010-P8). Fail closed.
    const links = screen.getAllByRole('link');
    expect(links.filter((link) => link.hasAttribute('href'))).toHaveLength(1);
    for (const link of links.filter((l) => !l.hasAttribute('href'))) {
      expect(link).toHaveAttribute('aria-disabled', 'true');
    }
    expect(screen.getAllByText('locked').length).toBeGreaterThan(0);
  });

  it('still renders denied items, so the shape of the product stays legible', () => {
    render(<Navigation can={() => false} />);

    expect(screen.getByText('Administration')).toBeInTheDocument();
  });

  it('marks the active route with aria-current, and only that route', () => {
    rtlRender(
      <MemoryRouter initialEntries={['/deals']}>
        <Navigation can={holding(Permissions.DealsRead, Permissions.AccountsRead)} />
      </MemoryRouter>,
    );

    expect(screen.getByRole('link', { name: /^Deals$/ })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: /^Accounts$/ })).not.toHaveAttribute('aria-current');
    expect(screen.getByRole('link', { name: 'Overview' })).not.toHaveAttribute('aria-current');
  });

  it('gates on the exact permission string, never a prefix', () => {
    // 'orders.read' must not be satisfied by holding 'orders.credit.override' or anything
    // that merely starts with 'orders'. Permissions are ordinal, exact strings.
    render(<Navigation can={holding(Permissions.OrdersCreditOverride)} />);

    expect(screen.getByText('Orders').closest('a')).toHaveAttribute('aria-disabled', 'true');
  });
});
