import { useState, type FormEvent } from 'react';
import type { ContactView } from '../../api';
import { useAccounts } from '../../data/useAccounts';
import { useCreateContact } from '../../data/useContacts';
import { Permissions } from '../../permissions';
import { useSingleFlight } from '../useSingleFlight';
import {
  EMPTY_CONTACT_DRAFT,
  describeContactError,
  toCreateContact,
  type ContactDraft,
} from './formModel';

const LABELS: Record<keyof ContactDraft, string> = {
  accountId: 'Account ID',
  name: 'Name',
  email: 'Email',
  phone: 'Phone',
  messenger: 'Messenger',
};

/**
 * Create a contact under an account (`POST /api/accounts/{id}/contacts`). The account id is typed
 * or picked; when the viewer also holds `accounts.read`, the accounts they can see are offered as
 * suggestions (the accounts hook is gated on that permission itself, so without it nothing is
 * fetched and the field is a plain id input). Whether the account is in scope is the server's
 * answer — a 404 is shown in the server's own words.
 */
export function CreateContactPanel({
  canWrite,
  initialAccountId,
  onClose,
  onCreated,
}: {
  canWrite: boolean;
  initialAccountId: string;
  onClose: () => void;
  onCreated: (contact: ContactView) => void;
}) {
  const [draft, setDraft] = useState<ContactDraft>({
    ...EMPTY_CONTACT_DRAFT,
    accountId: initialAccountId,
  });
  const [problems, setProblems] = useState<string[]>([]);
  const create = useCreateContact();
  const flight = useSingleFlight();
  const accounts = useAccounts({ limit: 50 });
  const suggestions = accounts.grid.kind === 'rows' ? accounts.grid.rows : [];

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const converted = toCreateContact(draft);
    if (!converted.ok) {
      setProblems(converted.problems);
      return;
    }
    setProblems([]);
    if (!flight.begin()) return;
    create.mutate(
      { accountId: converted.accountId, body: converted.request },
      {
        onSuccess: (contact) => onCreated(contact),
        onSettled: () => flight.end(),
      },
    );
  };

  const failure = create.error ? describeContactError(create.error, 'create') : null;
  const disabled = !canWrite || create.isPending;

  const field = (key: keyof ContactDraft, extra: { type?: string; list?: string } = {}) => (
    <label className="field" key={key}>
      <span>{LABELS[key]}</span>
      <input
        name={key}
        value={draft[key]}
        disabled={disabled}
        className={key === 'accountId' || key === 'phone' ? 'mono' : undefined}
        onChange={(event) => setDraft({ ...draft, [key]: event.target.value })}
        {...extra}
      />
    </label>
  );

  return (
    <aside className="card side-panel" aria-label="New contact">
      <header className="panel-head">
        <h2>New contact</h2>
        <button type="button" className="icon-btn" onClick={onClose} aria-label="Close panel">
          ×
        </button>
      </header>
      <form onSubmit={submit} noValidate>
        <fieldset className="fields" disabled={disabled}>
          {field('accountId', suggestions.length > 0 ? { list: 'contact-account-options' } : {})}
          {suggestions.length > 0 && (
            <datalist id="contact-account-options">
              {suggestions.map((account) => (
                <option key={account.id} value={account.id}>
                  {account.name}
                </option>
              ))}
            </datalist>
          )}
          {field('name')}
          {field('email', { type: 'email' })}
          <div className="field-pair">
            {field('phone')}
            {field('messenger')}
          </div>
        </fieldset>

        {problems.length > 0 && (
          <ul className="problems" role="alert">
            {problems.map((problem) => (
              <li key={problem}>{problem}</li>
            ))}
          </ul>
        )}
        {failure && (
          <p className={`notice notice-${failure.kind}`} role="alert" data-testid="create-failure">
            {failure.message}
          </p>
        )}

        <div className="form-actions">
          <button
            type="submit"
            className="btn primary"
            disabled={disabled}
            title={canWrite ? undefined : `Requires ${Permissions.ContactsWrite}`}
          >
            {create.isPending ? 'Creating…' : 'Create contact'}
          </button>
          <button type="button" className="btn ghost" onClick={onClose}>
            Cancel
          </button>
        </div>
      </form>
    </aside>
  );
}
