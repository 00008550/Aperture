import { useState, type FormEvent, type ReactNode } from 'react';
import type { DealView } from '../../api';
import { useAddDealLine, useCreateDeal, useDeal } from '../../data/useDeals';
import { Permissions } from '../../permissions';
import { AccountName } from '../AccountName';
import { useSingleFlight } from '../useSingleFlight';
import { StageChip } from './DealsGrid';
import {
  EMPTY_DEAL_DRAFT,
  EMPTY_LINE_DRAFT,
  date,
  describeDealError,
  lineTotal,
  money,
  percent,
  toAddLine,
  toCreateDeal,
  type DealDraft,
  type LineDraft,
} from './formModel';

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

const DEAL_LABELS: Record<keyof DealDraft, string> = {
  accountId: 'Account ID',
  name: 'Name',
  amount: 'Amount',
  discountPct: 'Discount %',
};

/**
 * Open a deal (`POST /api/deals`). The account is named in the body and the server validates it is
 * in the caller's scope — a 404 is shown in the server's own words. When the viewer can read
 * accounts, the accounts they can see are offered as suggestions (the accounts hook is gated on
 * that permission itself, so without it nothing is fetched).
 */
export function CreateDealPanel({
  canWrite,
  initialAccountId,
  accountOptions,
  onClose,
  onCreated,
}: {
  canWrite: boolean;
  initialAccountId: string;
  accountOptions: { id: string; name: string }[];
  onClose: () => void;
  onCreated: (deal: DealView) => void;
}) {
  const [draft, setDraft] = useState<DealDraft>({
    ...EMPTY_DEAL_DRAFT,
    accountId: initialAccountId,
  });
  const [problems, setProblems] = useState<string[]>([]);
  const create = useCreateDeal();
  const flight = useSingleFlight();

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const converted = toCreateDeal(draft);
    if (!converted.ok) {
      setProblems(converted.problems);
      return;
    }
    setProblems([]);
    if (!flight.begin()) return;
    create.mutate(converted.request, {
      onSuccess: (deal) => onCreated(deal as DealView),
      onSettled: () => flight.end(),
    });
  };

  const failure = create.error ? describeDealError(create.error, 'create') : null;
  const disabled = !canWrite || create.isPending;

  const field = (
    key: keyof DealDraft,
    extra: { inputMode?: 'decimal'; list?: string; mono?: boolean } = {},
  ) => (
    <label className="field" key={key}>
      <span>{DEAL_LABELS[key]}</span>
      <input
        name={key}
        value={draft[key]}
        disabled={disabled}
        className={extra.mono ? 'mono' : undefined}
        inputMode={extra.inputMode}
        list={extra.list}
        onChange={(event) => setDraft({ ...draft, [key]: event.target.value })}
      />
    </label>
  );

  return (
    <Panel title="New deal" onClose={onClose}>
      <form onSubmit={submit} noValidate>
        <fieldset className="fields" disabled={disabled}>
          {field('accountId', {
            mono: true,
            ...(accountOptions.length > 0 ? { list: 'deal-account-options' } : {}),
          })}
          {accountOptions.length > 0 && (
            <datalist id="deal-account-options">
              {accountOptions.map((account) => (
                <option key={account.id} value={account.id}>
                  {account.name}
                </option>
              ))}
            </datalist>
          )}
          {field('name')}
          <div className="field-pair">
            {field('amount', { inputMode: 'decimal', mono: true })}
            {field('discountPct', { inputMode: 'decimal', mono: true })}
          </div>
        </fieldset>
        <p className="sub form-hint">A new deal always opens in the <b>new</b> stage.</p>

        <Problems problems={problems} />
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
            title={canWrite ? undefined : `Requires ${Permissions.DealsWrite}`}
          >
            {create.isPending ? 'Creating…' : 'Create deal'}
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

/**
 * One deal, read from `GET /api/deals/{id}` — the only read that carries lines. The panel renders
 * the server's copy and nothing else: after an add-line the `deals` namespace is invalidated and
 * this detail refetches, so the lines shown are always what the server now holds.
 */
export function DealDetailPanel({
  id,
  canWrite,
  accountName,
  onClose,
}: {
  id: string;
  canWrite: boolean;
  accountName: (id: string) => string | null;
  onClose: () => void;
}) {
  const detail = useDeal(id);
  const gone =
    detail.error !== null && describeDealError(detail.error, 'add-line').kind === 'gone';

  return (
    <Panel title="Deal" onClose={onClose}>
      {detail.isPending && detail.fetchStatus !== 'idle' && (
        <p className="sub" role="status">
          Loading deal…
        </p>
      )}
      {detail.error && (
        <p className="notice notice-gone" role="alert">
          {gone ? 'This deal is not visible to you.' : 'The deal could not be loaded.'}
        </p>
      )}
      {detail.data && (
        <DealDetail
          deal={detail.data}
          refreshing={detail.isFetching}
          canWrite={canWrite}
          accountName={accountName}
        />
      )}
    </Panel>
  );
}

function DealDetail({
  deal,
  refreshing,
  canWrite,
  accountName,
}: {
  deal: DealView;
  refreshing: boolean;
  canWrite: boolean;
  accountName: (id: string) => string | null;
}) {
  const total = deal.lines.reduce((sum, line) => sum + lineTotal(line), 0);

  return (
    <div className="deal-detail" data-testid="deal-detail">
      <div className="deal-title">
        <h3>{deal.name}</h3>
        <StageChip stage={deal.stage} pending={deal.pendingApproval} />
      </div>
      <p className="sub mono version-line">
        version {deal.version}
        {refreshing && <span className="pulse-dot" aria-label="Refreshing" />}
      </p>

      <dl className="kv">
        <div>
          <dt>Account</dt>
          <dd>
            <AccountName id={deal.accountId} name={accountName(deal.accountId)} />
          </dd>
        </div>
        <div>
          <dt>Amount</dt>
          <dd className="mono">{money.format(deal.amount)}</dd>
        </div>
        <div>
          <dt>Discount</dt>
          <dd className="mono">{percent.format(deal.discountPct)}%</dd>
        </div>
        <div>
          <dt>Price list</dt>
          <dd className="mono">{deal.frozenPriceListVersion ?? '—'}</dd>
        </div>
        <div>
          <dt>Created</dt>
          <dd>{date.format(new Date(deal.createdAt))}</dd>
        </div>
      </dl>

      <section className="lines" aria-label="Lines">
        <h4>
          Lines <span className="sub mono">{deal.lines.length}</span>
        </h4>
        {deal.lines.length === 0 ? (
          <p className="sub" data-testid="no-lines">
            No lines yet.
          </p>
        ) : (
          <table className="lines-table" aria-label="Deal lines">
            <thead>
              <tr>
                <th scope="col">Product</th>
                <th scope="col" className="num">
                  Qty
                </th>
                <th scope="col" className="num">
                  Unit price
                </th>
                <th scope="col" className="num">
                  Total
                </th>
                <th scope="col">Price list</th>
              </tr>
            </thead>
            <tbody>
              {deal.lines.map((line) => (
                <tr key={line.id} data-testid={`line-${line.id}`}>
                  <td className="mono">{line.productRef}</td>
                  <td className="mono num">{line.quantity}</td>
                  <td className="mono num">{money.format(line.unitPrice)}</td>
                  <td className="mono num">{money.format(lineTotal(line))}</td>
                  <td className="mono sub-cell">{line.priceListVersion ?? '—'}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th scope="row" colSpan={3}>
                  Lines total
                </th>
                <td className="mono num" data-testid="lines-total">
                  {money.format(total)}
                </td>
                <td />
              </tr>
            </tfoot>
          </table>
        )}
      </section>

      {/* Keyed on the deal: switching deals never carries a half-typed line across. */}
      <AddLineForm key={deal.id} dealId={deal.id} canWrite={canWrite} />
    </div>
  );
}

const LINE_LABELS: Record<keyof LineDraft, string> = {
  productRef: 'Product',
  unitPrice: 'Unit price',
  quantity: 'Quantity',
  priceListVersion: 'Price-list version',
};

function AddLineForm({ dealId, canWrite }: { dealId: string; canWrite: boolean }) {
  const [draft, setDraft] = useState<LineDraft>(EMPTY_LINE_DRAFT);
  const [problems, setProblems] = useState<string[]>([]);
  const [added, setAdded] = useState(false);
  const add = useAddDealLine();
  const flight = useSingleFlight();

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const converted = toAddLine(draft);
    if (!converted.ok) {
      setProblems(converted.problems);
      return;
    }
    setProblems([]);
    setAdded(false);
    if (!flight.begin()) return;
    add.mutate(
      { dealId, body: converted.request },
      {
        onSuccess: () => {
          setDraft(EMPTY_LINE_DRAFT);
          setAdded(true);
        },
        onSettled: () => flight.end(),
      },
    );
  };

  const failure = add.error ? describeDealError(add.error, 'add-line') : null;
  const disabled = !canWrite || add.isPending;

  const field = (key: keyof LineDraft, extra: { inputMode?: 'decimal' | 'numeric' } = {}) => (
    <label className="field" key={key}>
      <span>{LINE_LABELS[key]}</span>
      <input
        name={key}
        value={draft[key]}
        disabled={disabled}
        className={key === 'productRef' ? undefined : 'mono'}
        inputMode={extra.inputMode}
        onChange={(event) => {
          setDraft({ ...draft, [key]: event.target.value });
          setAdded(false);
        }}
      />
    </label>
  );

  return (
    <form className="add-line" onSubmit={submit} noValidate aria-label="Add line">
      <h4>Add line</h4>
      <fieldset className="fields" disabled={disabled}>
        {field('productRef')}
        <div className="field-pair">
          {field('quantity', { inputMode: 'numeric' })}
          {field('unitPrice', { inputMode: 'decimal' })}
        </div>
        {field('priceListVersion')}
      </fieldset>

      <Problems problems={problems} />
      {failure && (
        <p className={`notice notice-${failure.kind}`} role="alert" data-testid="add-line-failure">
          {failure.message}
        </p>
      )}
      {added && !failure && (
        <p className="notice notice-ok" role="status">
          Line added.
        </p>
      )}

      <div className="form-actions">
        <button
          type="submit"
          className="btn primary"
          disabled={disabled}
          title={canWrite ? undefined : `Requires ${Permissions.DealsWrite}`}
        >
          {add.isPending ? 'Adding…' : 'Add line'}
        </button>
      </div>
    </form>
  );
}
