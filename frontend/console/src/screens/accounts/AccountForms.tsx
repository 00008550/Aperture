import { useState, type FormEvent, type ReactNode } from 'react';
import type { AccountView } from '../../api';
import { useAccount, useCreateAccount, useUpdateAccount } from '../../data/useAccounts';
import { Permissions } from '../../permissions';
import { useSingleFlight } from '../useSingleFlight';
import {
  EMPTY_DRAFT,
  changedFields,
  describeWriteError,
  draftFromAccount,
  toCreateRequest,
  toUpdateRequest,
  type AccountDraft,
} from './formModel';

const FIELD_LABELS: Record<keyof AccountDraft, string> = {
  name: 'Name',
  taxId: 'Tax ID',
  creditLimit: 'Credit limit',
  paymentTermsDays: 'Payment terms (days)',
  regionId: 'Region ID',
  teamId: 'Team ID',
};

function Panel({
  title,
  onClose,
  children,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
}) {
  return (
    <aside className="card side-panel" aria-label={title}>
      <header className="panel-head">
        <h2>{title}</h2>
        <button type="button" className="icon-btn" onClick={onClose} aria-label="Close panel">
          ×
        </button>
      </header>
      {children}
    </aside>
  );
}

function Fields({
  draft,
  onChange,
  disabled,
  taxIdReadOnly,
}: {
  draft: AccountDraft;
  onChange: (next: AccountDraft) => void;
  disabled: boolean;
  taxIdReadOnly: boolean;
}) {
  const field = (key: keyof AccountDraft, extra: { inputMode?: 'decimal' | 'numeric' } = {}) => (
    <label className="field" key={key}>
      <span>{FIELD_LABELS[key]}</span>
      <input
        name={key}
        value={draft[key]}
        disabled={disabled}
        readOnly={key === 'taxId' && taxIdReadOnly}
        className={key === 'regionId' || key === 'teamId' || key === 'taxId' ? 'mono' : undefined}
        onChange={(event) => onChange({ ...draft, [key]: event.target.value })}
        {...extra}
      />
    </label>
  );

  return (
    <fieldset className="fields" disabled={disabled}>
      {field('name')}
      {field('taxId')}
      <div className="field-pair">
        {field('creditLimit', { inputMode: 'decimal' })}
        {field('paymentTermsDays', { inputMode: 'numeric' })}
      </div>
      {field('regionId')}
      {field('teamId')}
    </fieldset>
  );
}

function Problems({ problems }: { problems: string[] }) {
  if (problems.length === 0) return null;
  return (
    <ul className="problems" role="alert">
      {problems.map((problem) => (
        <li key={problem}>{problem}</li>
      ))}
    </ul>
  );
}

// ---------------------------------------------------------------------------------------------

export function CreateAccountPanel({
  canWrite,
  onClose,
  onCreated,
}: {
  canWrite: boolean;
  onClose: () => void;
  onCreated: (account: AccountView) => void;
}) {
  const [draft, setDraft] = useState<AccountDraft>(EMPTY_DRAFT);
  const [problems, setProblems] = useState<string[]>([]);
  const create = useCreateAccount();
  const flight = useSingleFlight();

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const converted = toCreateRequest(draft);
    if (!converted.ok) {
      setProblems(converted.problems);
      return;
    }
    setProblems([]);
    if (!flight.begin()) return;
    create.mutate(converted.request, {
      onSuccess: (account) => onCreated(account),
      onSettled: () => flight.end(),
    });
  };

  const failure = create.error ? describeWriteError(create.error, 'create') : null;

  return (
    <Panel title="New account" onClose={onClose}>
      <form onSubmit={submit} noValidate>
        <Fields
          draft={draft}
          onChange={setDraft}
          disabled={!canWrite || create.isPending}
          taxIdReadOnly={false}
        />
        <Problems problems={problems} />
        {failure && (
          <p className={`notice notice-${failure.kind}`} role="alert">
            {failure.message}
          </p>
        )}
        <div className="form-actions">
          <button
            type="submit"
            className="btn primary"
            disabled={!canWrite || create.isPending}
            title={canWrite ? undefined : `Requires ${Permissions.AccountsWrite}`}
          >
            {create.isPending ? 'Creating…' : 'Create account'}
          </button>
          <button type="button" className="btn ghost" onClick={onClose}>
            Cancel
          </button>
        </div>
      </form>
    </Panel>
  );
}

// ---------------------------------------------------------------------------------------------

export function EditAccountPanel({
  id,
  canWrite,
  onClose,
}: {
  id: string;
  canWrite: boolean;
  onClose: () => void;
}) {
  const detail = useAccount(id);

  return (
    <Panel title="Account" onClose={onClose}>
      {detail.isPending && detail.fetchStatus !== 'idle' && (
        <p className="sub" role="status">
          Loading account…
        </p>
      )}
      {detail.error && (
        <p className="notice notice-gone" role="alert">
          {describeWriteError(detail.error, 'edit').kind === 'gone'
            ? 'This account is not visible to you.'
            : 'The account could not be loaded.'}
        </p>
      )}
      {detail.data && (
        <EditAccountForm
          account={detail.data}
          refreshing={detail.isFetching}
          canWrite={canWrite}
        />
      )}
    </Panel>
  );
}

/**
 * The edit form. It holds the user's draft and the version that draft was based on; the server's
 * copy (`account`) can move underneath it. On a 409 the draft is kept, the hook refetches the
 * account, and the user chooses: re-apply their changes onto the version they can now see, or
 * discard them. The write is never resubmitted without that choice (edge 7).
 */
function EditAccountForm({
  account,
  refreshing,
  canWrite,
}: {
  account: AccountView;
  refreshing: boolean;
  canWrite: boolean;
}) {
  const [draft, setDraft] = useState<AccountDraft>(() => draftFromAccount(account));
  const [baseVersion, setBaseVersion] = useState(account.version);
  const [problems, setProblems] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);
  const update = useUpdateAccount();
  const flight = useSingleFlight();

  const failure = update.error ? describeWriteError(update.error, 'edit') : null;
  const conflicted = failure?.kind === 'conflict';
  const busy = update.isPending;

  const send = (expectedVersion: number) => {
    const converted = toUpdateRequest(draft, account.ownerUserId, expectedVersion);
    if (!converted.ok) {
      setProblems(converted.problems);
      return;
    }
    setProblems([]);
    if (!flight.begin()) return;
    setBaseVersion(expectedVersion);
    update.mutate(
      { id: account.id, body: converted.request },
      {
        onSuccess: (next) => {
          setDraft(draftFromAccount(next));
          setBaseVersion(next.version);
          setSaved(true);
        },
        onSettled: () => flight.end(),
      },
    );
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    // While conflicted the plain submit is not offered — only the explicit re-apply below.
    if (conflicted) return;
    send(baseVersion);
  };

  const discard = () => {
    setDraft(draftFromAccount(account));
    setBaseVersion(account.version);
    setProblems([]);
    update.reset();
  };

  const edit = (next: AccountDraft) => {
    setDraft(next);
    setSaved(false);
  };

  const differing = conflicted ? changedFields(draft, account) : [];

  return (
    <form onSubmit={submit} noValidate data-conflict={conflicted}>
      <p className="sub mono version-line">
        version {baseVersion}
        {refreshing && <span className="pulse-dot" aria-label="Refreshing" />}
      </p>

      {conflicted && (
        <div className="notice notice-conflict" role="alert" data-testid="conflict">
          <strong>Changed by someone else.</strong>
          <p>{failure?.message}</p>
          <p className="sub">
            {refreshing
              ? 'Loading the latest version…'
              : differing.length > 0
                ? `The server now differs in: ${differing.map((key) => FIELD_LABELS[key]).join(', ')}.`
                : 'The latest version is loaded; your changes are kept below.'}
            {!refreshing && <span className="mono"> (now version {account.version})</span>}
          </p>
        </div>
      )}

      <Fields draft={draft} onChange={edit} disabled={!canWrite || busy} taxIdReadOnly />
      <Problems problems={problems} />

      {failure && !conflicted && (
        <p className={`notice notice-${failure.kind}`} role="alert">
          {failure.message}
        </p>
      )}
      {saved && !failure && (
        <p className="notice notice-ok" role="status">
          Saved.
        </p>
      )}

      <div className="form-actions">
        {conflicted ? (
          <>
            <button
              type="button"
              className="btn primary"
              disabled={!canWrite || busy || refreshing}
              onClick={() => send(account.version)}
            >
              Re-apply my changes
            </button>
            <button type="button" className="btn ghost" onClick={discard} disabled={busy}>
              Discard mine, keep theirs
            </button>
          </>
        ) : (
          <button
            type="submit"
            className="btn primary"
            disabled={!canWrite || busy}
            title={canWrite ? undefined : `Requires ${Permissions.AccountsWrite}`}
          >
            {busy ? 'Saving…' : 'Save changes'}
          </button>
        )}
      </div>
    </form>
  );
}
