import type { CSSProperties, KeyboardEvent } from 'react';
import type { DealView } from '../../api';
import type { GridModel } from '../../data/gate';
import { Permissions } from '../../permissions';
import { AccountName } from '../AccountName';
import { date, money, percent } from './formModel';

export interface DealsGridProps {
  grid: GridModel<DealView>;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onLoadMore: () => void;
  loadingMore: boolean;
  onRetry: () => void;
  accountName: (id: string) => string | null;
}

/**
 * The stage as a chip. The stage string is the server's (`new`, `qualified`, `quoted`,
 * `negotiation`, `won`, `lost`); the chip only colours it — an unknown stage still renders, in the
 * neutral style, so a stage the server adds later is shown rather than hidden.
 */
export function StageChip({ stage, pending }: { stage: string; pending?: boolean }) {
  return (
    <span className="stage-chip" data-stage={stage.toLowerCase()} data-testid="stage-chip">
      <span className="stage-dot" aria-hidden="true" />
      {stage}
      {pending && <span className="visually-hidden"> (discount pending approval)</span>}
    </span>
  );
}

/**
 * The deals grid, rendered from the one `GridModel` the data layer decided — the same stated
 * surfaces as the accounts and contacts grids. The list endpoint returns deals *without* their
 * lines, so the grid never shows a line count it does not have; lines live on the detail.
 */
export function DealsGrid(props: DealsGridProps) {
  const { grid } = props;

  switch (grid.kind) {
    case 'denied':
      return (
        <section className="card grid-state" data-grid-state="denied">
          <p className="lock-line">
            <span className="lock-glyph" aria-hidden="true">
              ◇
            </span>
            <span>You do not have access to deals.</span>
          </p>
          <p className="sub mono">Requires {Permissions.DealsRead}</p>
        </section>
      );

    case 'no-scope':
      return (
        <section className="card grid-state" data-grid-state="no-scope">
          <div className="empty" role="status">
            <strong className="warn">No data scopes granted.</strong>
            <p className="sub">
              This is not an empty list — nothing is visible to you at all. Deals appear only once
              an administrator grants you a data scope.
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
          aria-label="Deals"
        >
          <p className="sub visually-hidden" role="status">
            Loading deals…
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
            The deals could not be loaded.
          </p>
          <button type="button" className="btn" onClick={props.onRetry}>
            Try again
          </button>
        </section>
      );

    case 'empty':
      return (
        <section className="card grid-state" data-grid-state="empty">
          <p role="status">No deals yet.</p>
          <p className="sub">Your scopes are in effect and nothing in them matches.</p>
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
  accountName,
}: DealsGridProps & { rows: DealView[]; hasMore: boolean }) {
  const onKey = (event: KeyboardEvent<HTMLTableRowElement>, id: string) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      onSelect(id);
    }
  };

  return (
    <section className="card data-grid-card" data-grid-state="rows">
      <table className="data-grid" aria-label="Deals">
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col">Account</th>
            <th scope="col">Stage</th>
            <th scope="col" className="num">
              Amount
            </th>
            <th scope="col" className="num">
              Discount
            </th>
            <th scope="col">Created</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((deal, index) => {
            const selected = deal.id === selectedId;
            return (
              <tr
                key={deal.id}
                tabIndex={0}
                aria-selected={selected}
                data-selected={selected}
                data-testid={`deal-row-${deal.id}`}
                style={{ '--i': Math.min(index, 12) } as CSSProperties}
                onClick={() => onSelect(deal.id)}
                onKeyDown={(event) => onKey(event, deal.id)}
              >
                <td className="name-cell">
                  <span className="row-mark" aria-hidden="true" />
                  {deal.name}
                </td>
                <td className="sub-cell">
                  <AccountName id={deal.accountId} name={accountName(deal.accountId)} />
                </td>
                <td>
                  <StageChip stage={deal.stage} pending={deal.pendingApproval} />
                </td>
                <td className="mono num">{money.format(deal.amount)}</td>
                <td className="mono num">{percent.format(deal.discountPct)}%</td>
                <td className="sub-cell">{date.format(new Date(deal.createdAt))}</td>
              </tr>
            );
          })}
        </tbody>
      </table>

      <footer className="grid-foot">
        <span className="sub mono">
          {rows.length} {rows.length === 1 ? 'deal' : 'deals'}
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
