import type { CSSProperties, KeyboardEvent } from 'react';
import type { ContactView } from '../../api';
import type { GridModel } from '../../data/gate';
import { Permissions } from '../../permissions';

const date = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric' });

export interface ContactsGridProps {
  grid: GridModel<ContactView>;
  includeDeparted: boolean;
  selectedId: string | null;
  onSelect: (contact: ContactView) => void;
  canWrite: boolean;
  /** The contact whose depart is awaiting confirmation, if any. */
  confirmingId: string | null;
  onAskDepart: (id: string | null) => void;
  onConfirmDepart: (id: string) => void;
  /** True while any depart is in flight — every depart control is disabled (edge 10). */
  departing: boolean;
  onLoadMore: () => void;
  loadingMore: boolean;
  onRetry: () => void;
}

/**
 * The contacts grid, rendered from the one `GridModel` the data layer decided (`data/gate.ts`) —
 * the same stated surfaces as the accounts grid. A departed contact is never removed from
 * history: under `includeDeparted` it stays in the grid, visibly marked departed (edge 12).
 */
export function ContactsGrid(props: ContactsGridProps) {
  const { grid } = props;

  switch (grid.kind) {
    case 'denied':
      return (
        <section className="card grid-state" data-grid-state="denied">
          <p className="lock-line">
            <span className="lock-glyph" aria-hidden="true">
              ◇
            </span>
            <span>You do not have access to contacts.</span>
          </p>
          <p className="sub mono">Requires {Permissions.ContactsRead}</p>
        </section>
      );

    case 'no-scope':
      return (
        <section className="card grid-state" data-grid-state="no-scope">
          <div className="empty" role="status">
            <strong className="warn">No data scopes granted.</strong>
            <p className="sub">
              This is not an empty list — nothing is visible to you at all. Contacts appear only
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
          aria-label="Contacts"
        >
          <p className="sub visually-hidden" role="status">
            Loading contacts…
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
            The contacts could not be loaded.
          </p>
          <button type="button" className="btn" onClick={props.onRetry}>
            Try again
          </button>
        </section>
      );

    case 'empty':
      return (
        <section className="card grid-state" data-grid-state="empty">
          <p role="status">{props.includeDeparted ? 'No contacts yet.' : 'No active contacts.'}</p>
          <p className="sub">
            Your scopes are in effect and nothing in them matches
            {props.includeDeparted ? '.' : ' — departed contacts are hidden.'}
          </p>
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
  canWrite,
  confirmingId,
  onAskDepart,
  onConfirmDepart,
  departing,
  onLoadMore,
  loadingMore,
}: ContactsGridProps & { rows: ContactView[]; hasMore: boolean }) {
  const onKey = (event: KeyboardEvent<HTMLTableRowElement>, contact: ContactView) => {
    // Only the row itself selects; keys pressed on its buttons act on the buttons.
    if (event.target !== event.currentTarget) return;
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      onSelect(contact);
    }
  };

  return (
    <section className="card data-grid-card" data-grid-state="rows">
      <table className="data-grid" aria-label="Contacts">
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col">Email</th>
            <th scope="col">Phone</th>
            <th scope="col">Account</th>
            <th scope="col">Created</th>
            <th scope="col" className="num">
              <span className="visually-hidden">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((contact, index) => {
            const selected = contact.id === selectedId;
            const confirming = contact.id === confirmingId;
            return (
              <tr
                key={contact.id}
                tabIndex={0}
                aria-selected={selected}
                data-selected={selected}
                data-departed={contact.isDeparted}
                data-testid={`contact-row-${contact.id}`}
                style={{ '--i': Math.min(index, 12) } as CSSProperties}
                onClick={() => onSelect(contact)}
                onKeyDown={(event) => onKey(event, contact)}
              >
                <td className="name-cell">
                  <span className="row-mark" aria-hidden="true" />
                  {contact.name}
                  {contact.isDeparted && (
                    <span className="badge badge-departed" data-testid="departed-badge">
                      Departed
                      {contact.departedAt ? ` ${date.format(new Date(contact.departedAt))}` : ''}
                    </span>
                  )}
                </td>
                <td className="sub-cell">{contact.email ?? '—'}</td>
                <td className="mono sub-cell">{contact.phone ?? '—'}</td>
                <td className="mono sub-cell" title={contact.accountId}>
                  {contact.accountId.slice(0, 8)}
                </td>
                <td className="sub-cell">{date.format(new Date(contact.createdAt))}</td>
                <td className="num row-actions" onClick={(event) => event.stopPropagation()}>
                  {contact.isDeparted ? null : confirming ? (
                    <span className="confirm-pair">
                      <button
                        type="button"
                        className="btn danger small"
                        disabled={!canWrite || departing}
                        onClick={() => onConfirmDepart(contact.id)}
                      >
                        {departing ? 'Departing…' : 'Confirm depart'}
                      </button>
                      <button
                        type="button"
                        className="btn ghost small"
                        disabled={departing}
                        onClick={() => onAskDepart(null)}
                      >
                        Keep
                      </button>
                    </span>
                  ) : (
                    <button
                      type="button"
                      className="btn ghost small"
                      disabled={!canWrite || departing}
                      title={canWrite ? undefined : `Requires ${Permissions.ContactsWrite}`}
                      aria-label={`Depart ${contact.name}`}
                      onClick={() => onAskDepart(contact.id)}
                    >
                      Depart
                    </button>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>

      <footer className="grid-foot">
        <span className="sub mono">
          {rows.length} {rows.length === 1 ? 'contact' : 'contacts'}
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
