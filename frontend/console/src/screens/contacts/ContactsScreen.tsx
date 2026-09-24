import { useState } from 'react';
import type { ContactView } from '../../api';
import { useContacts, useDepartContact } from '../../data/useContacts';
import { Permissions } from '../../permissions';
import { useSession } from '../../useSession';
import { useAccountLookup } from '../AccountName';
import { useSingleFlight } from '../useSingleFlight';
import { ContactsGrid } from './ContactsGrid';
import { CreateContactPanel } from './CreateContactPanel';
import { describeContactError } from './formModel';

/** Rows per keyset page. */
export const CONTACTS_PAGE_SIZE = 25;

/**
 * The Contacts screen. The route guard has already asked `can('contacts.read')` before this
 * mounts; both writes ask `can('contacts.write')` the same fail-closed way and are disabled —
 * never hidden — without it.
 *
 * Departing is not deleting (edge 12): the server marks the row departed and the active list
 * (the default) stops returning it; the "Show departed" switch asks the server for
 * `includeDeparted=true`, where the row is still there, marked. The switch is a query parameter,
 * not a client-side filter — the grid never hides or reveals rows the server did not decide on.
 *
 * There is no single-contact GET on the API, so there is no detail panel: selecting a row only
 * picks the account a new contact is created under.
 */
export function ContactsScreen() {
  const { can } = useSession();
  const canWrite = can(Permissions.ContactsWrite);
  const [includeDeparted, setIncludeDeparted] = useState(false);
  const [creating, setCreating] = useState(false);
  const [selected, setSelected] = useState<ContactView | null>(null);
  const [confirmingId, setConfirmingId] = useState<string | null>(null);
  const [departedName, setDepartedName] = useState<string | null>(null);

  const contacts = useContacts({ limit: CONTACTS_PAGE_SIZE, includeDeparted });
  const depart = useDepartContact();
  const flight = useSingleFlight();
  const accountName = useAccountLookup().nameOf;

  const confirmDepart = (id: string) => {
    if (!flight.begin()) return;
    setDepartedName(null);
    depart.mutate(id, {
      onSuccess: (contact) => {
        setConfirmingId(null);
        setDepartedName(contact.name);
        if (selected?.id === contact.id) setSelected(null);
      },
      onSettled: () => flight.end(),
    });
  };

  const departFailure = depart.error ? describeContactError(depart.error, 'depart') : null;

  return (
    <div className="screen">
      <header className="screen-head">
        <div>
          <h1>Contacts</h1>
          <p className="sub">
            People at accounts in your scopes, from <span className="mono">GET /api/contacts</span>.
          </p>
        </div>
        <div className="head-actions">
          <label className="switch">
            <input
              type="checkbox"
              role="switch"
              checked={includeDeparted}
              onChange={(event) => {
                setIncludeDeparted(event.target.checked);
                setConfirmingId(null);
              }}
            />
            <span className="switch-track" aria-hidden="true" />
            <span>Show departed</span>
          </label>
          <button
            type="button"
            className="btn primary"
            disabled={!canWrite}
            title={canWrite ? undefined : `Requires ${Permissions.ContactsWrite}`}
            onClick={() => setCreating(true)}
          >
            New contact
          </button>
        </div>
      </header>

      {departFailure && (
        <p className={`notice notice-${departFailure.kind}`} role="alert">
          {departFailure.message}
        </p>
      )}
      {departedName && !departFailure && (
        <p className="notice notice-ok" role="status">
          {departedName} is marked departed — kept for history
          {includeDeparted ? '.' : ', visible under “Show departed”.'}
        </p>
      )}

      <div className="screen-body" data-panel-open={creating}>
        <ContactsGrid
          grid={contacts.grid}
          includeDeparted={includeDeparted}
          selectedId={selected?.id ?? null}
          onSelect={setSelected}
          canWrite={canWrite}
          confirmingId={confirmingId}
          onAskDepart={(id) => {
            depart.reset();
            setConfirmingId(id);
          }}
          onConfirmDepart={confirmDepart}
          departing={depart.isPending}
          onLoadMore={() => void contacts.fetchNextPage()}
          loadingMore={contacts.isFetchingNextPage}
          onRetry={() => void contacts.refetch()}
          accountName={accountName}
        />

        {creating && (
          // The selected row's account seeds the draft once; later selections never overwrite
          // what the user has typed.
          <CreateContactPanel
            canWrite={canWrite}
            initialAccountId={selected?.accountId ?? ''}
            onClose={() => setCreating(false)}
            onCreated={() => setCreating(false)}
          />
        )}
      </div>
    </div>
  );
}
