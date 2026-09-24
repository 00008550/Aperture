import type { CSSProperties, KeyboardEvent } from 'react';
import type { AccountView } from '../../api';
import type { GridModel } from '../../data/gate';
import { Permissions } from '../../permissions';

const money = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 });
const date = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric' });

export interface AccountsGridProps {
  grid: GridModel<AccountView>;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onLoadMore: () => void;
  loadingMore: boolean;
  onRetry: () => void;
}

/**
 * The accounts grid, rendered from the one `GridModel` the data layer decided (`data/gate.ts`) —
 * the grid never re-derives "is this empty or am I blind?". Each state is its own, stated surface:
 * a user with no scopes sees "nothing is visible", never an empty table (edge 5, DOMAIN.md §5.1).
 */
export function AccountsGrid(props: AccountsGridProps) {
  const { grid } = props;

  switch (grid.kind) {
    case 'denied':
      return (
        <section className="card grid-state" data-grid-state="denied">
          <p className="lock-line">
            <span className="lock-glyph" aria-hidden="true">
              ◇
            </span>
            <span>You do not have access to accounts.</span>
          </p>
          <p className="sub mono">Requires {Permissions.AccountsRead}</p>
        </section>
      );

    case 'no-scope':
      return (
        <section className="card grid-state" data-grid-state="no-scope">
          <div className="empty" role="status">
            <strong className="warn">No data scopes granted.</strong>
            <p className="sub">
              This is not an empty list — nothing is visible to you at all. Accounts appear only
              once an administrator grants you a data scope.
            </p>
          </div>
        </section>
      );

    case 'loading':
      return (
        <section
          className="card data-grid-card"
          data-grid-state="loading"
          aria-busy="true"
          aria-label="Accounts"
        >
          <p className="sub visually-hidden" role="status">
            Loading accounts…
          </p>
          <div className="skeleton-rows" aria-hidden="true">
            {Array.from({ length: 6 }, (_, i) => (
              <div key={i} className="skeleton-row" style={{ '--i': i } as CSSProperties} />
            ))}
          </div>
        </section>
      );

    case 'error':
      return (
        <section className="card grid-state" data-grid-state="error">
          <p className="bad" role="alert">
            The accounts could not be loaded.
          </p>
          <button type="button" className="btn" onClick={props.onRetry}>
            Try again
          </button>
        </section>
      );

    case 'empty':
      return (
        <section className="card grid-state" data-grid-state="empty">
          <p role="status">No accounts yet.</p>
          <p className="sub">Your scopes are in effect and nothing in them exists yet.</p>
        </section>
      );

    case 'rows':
      return <Rows {...props} rows={grid.rows} hasMore={grid.hasMore} />;
  }
}

function Rows({
  rows,
  hasMore,
  selectedId,
  onSelect,
  onLoadMore,
  loadingMore,
}: AccountsGridProps & { rows: AccountView[]; hasMore: boolean }) {
  const onKey = (event: KeyboardEvent<HTMLTableRowElement>, id: string) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      onSelect(id);
    }
  };

  return (
    <section className="card data-grid-card" data-grid-state="rows">
      {/* The grid's name is its aria-label; how to use a row is its description (010-P8). */}
      <p id="accounts-grid-hint" className="visually-hidden">
        Each row opens its account. Focus a row with Tab, then press Enter or Space.
      </p>
      <table className="data-grid" aria-label="Accounts" aria-describedby="accounts-grid-hint">
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col">Tax ID</th>
            <th scope="col" className="num">
              Credit limit
            </th>
            <th scope="col" className="num">
              Terms
            </th>
            <th scope="col">Created</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((account, index) => {
            const selected = account.id === selectedId;
            return (
              <tr
                key={account.id}
                // A focused row is announced by this name (Chrome computes none for a table row), so
                // a keyboard user hears which record Enter would open (010-P8).
                aria-label={account.name}
                tabIndex={0}
                aria-selected={selected}
                data-selected={selected}
                // Stagger index for the entrance animation; capped so a long page does not crawl in.
                style={{ '--i': Math.min(index, 12) } as CSSProperties}
                onClick={() => onSelect(account.id)}
                onKeyDown={(event) => onKey(event, account.id)}
              >
                <td className="name-cell">
                  <span className="row-mark" aria-hidden="true" />
                  {account.name}
                </td>
                <td className="mono">{account.taxId}</td>
                <td className="mono num">{money.format(account.creditLimit)}</td>
                <td className="mono num">{account.paymentTermsDays}d</td>
                <td className="sub-cell">{date.format(new Date(account.createdAt))}</td>
              </tr>
            );
          })}
        </tbody>
      </table>

      <footer className="grid-foot">
        <span className="sub mono">
          {rows.length} {rows.length === 1 ? 'account' : 'accounts'}
          {hasMore ? ' loaded' : ''}
        </span>
        {/* A null cursor means the end: the control is gone, not merely disabled (edge 11). */}
        {hasMore && (
          <button type="button" className="btn" onClick={onLoadMore} disabled={loadingMore}>
            {loadingMore ? 'Loading…' : 'Load more'}
          </button>
        )}
      </footer>
    </section>
  );
}
