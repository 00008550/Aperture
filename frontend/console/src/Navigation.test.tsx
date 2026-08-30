import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Navigation } from './Navigation';
import { Permissions, type Permission } from './permissions';

const holding = (...held: Permission[]) => (p: Permission) => held.includes(p);

describe('the permission gate', () => {
  it('enables an item whose permission the user holds', () => {
    render(<Navigation can={holding(Permissions.DealsRead)} />);

    const deals = screen.getByRole('link', { name: /^Deals$/ });
    expect(deals).toHaveAttribute('href', '#deals');
    expect(deals).not.toHaveAttribute('aria-disabled');
  });

  it('disables an item whose permission the user lacks', () => {
    render(<Navigation can={holding(Permissions.DealsRead)} />);

    const orders = screen.getByText('Orders').closest('a');
    expect(orders).toHaveAttribute('aria-disabled', 'true');
    // No href: not a link, so it cannot be followed by keyboard or middle click either.
    expect(orders).not.toHaveAttribute('href');
    expect(orders).toHaveAttribute('title', `Requires ${Permissions.OrdersRead}`);
  });

  it('disables every item for a user with no permissions at all', () => {
    render(<Navigation can={() => false} />);

    // Overview is the only link; every gated item is denied. Fail closed.
    expect(screen.getAllByRole('link')).toHaveLength(1);
    expect(screen.getAllByText('locked').length).toBeGreaterThan(0);
  });

  it('still renders denied items, so the shape of the product stays legible', () => {
    render(<Navigation can={() => false} />);

    expect(screen.getByText('Administration')).toBeInTheDocument();
  });

  it('gates on the exact permission string, never a prefix', () => {
    // 'orders.read' must not be satisfied by holding 'orders.credit.override' or anything
    // that merely starts with 'orders'. Permissions are ordinal, exact strings.
    render(<Navigation can={holding(Permissions.OrdersCreditOverride)} />);

    expect(screen.getByText('Orders').closest('a')).toHaveAttribute('aria-disabled', 'true');
  });
});
