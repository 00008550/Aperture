import { useState, type FormEvent } from 'react';
import type { DealView } from '../../../api';
import { useApproveDealDiscount, useTransitionDeal } from '../../../data/useDeals';
import { Permissions } from '../../../permissions';
import { useSingleFlight } from '../../useSingleFlight';
import { percent } from '../formModel';
import {
  LOST_REASON_SUGGESTIONS,
  STAGE_LABEL,
  describeLifecycleError,
  isTerminal,
  nextStages,
  offeredMoves,
  toApproval,
  toTransition,
  type Stage,
} from './lifecycleModel';

/** The last write the user asked for — kept so a 409 can be re-applied, only on their say-so. */
type Attempt =
  | { kind: 'move'; target: Stage; reason: string; priceListVersion: string }
  | { kind: 'approve'; reason: string };

/**
 * The deal's lifecycle control (010-P7). It offers only the legal next stages from where the deal
 * is, sends every write with the version it was based on, and renders whatever the server answers:
 * the moved deal, a discount held for lead approval (`200` with `pendingApproval`), a `422` in the
 * server's words, or a `409` — the current deal, with an explicit re-apply / discard choice. It
 * never resends on its own and never shows a stage the server has not returned.
 *
 * `deal` is the detail query's copy; both writes put the server's answer into that cache, so after
 * any outcome this component is re-rendered from the server's deal.
 */
export function DealLifecycle({
  deal,
  canWrite,
  canApprove,
}: {
  deal: DealView;
  canWrite: boolean;
  canApprove: boolean;
}) {
  const transition = useTransitionDeal();
  const approve = useApproveDealDiscount();
  const flight = useSingleFlight();

  const [composing, setComposing] = useState<Stage | null>(null);
  const [reason, setReason] = useState('');
  const [priceListVersion, setPriceListVersion] = useState('');
  const [approvalReason, setApprovalReason] = useState('');
  const [problems, setProblems] = useState<string[]>([]);
  const [attempt, setAttempt] = useState<Attempt | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const active = attempt?.kind === 'approve' ? approve : transition;
  const failure = attempt && active.error ? describeLifecycleError(active.error) : null;
  const conflicted = failure?.kind === 'conflict';
  const busy = transition.isPending || approve.isPending;

  const reset = () => {
    transition.reset();
    approve.reset();
    setProblems([]);
  };

  const sendMove = (next: Extract<Attempt, { kind: 'move' }>, expectedVersion: number) => {
    const converted = toTransition(next.target, expectedVersion, next);
    if (!converted.ok) {
      setProblems(converted.problems);
      return;
    }
    if (!flight.begin()) return;
    reset();
    setDone(null);
    setAttempt(next);
    transition.mutate(
      { dealId: deal.id, body: converted.request },
      {
        onSuccess: (moved) => {
          setComposing(null);
          setReason('');
          setPriceListVersion('');
          setDone(
            moved.pendingApproval && next.target === 'won' && moved.stage !== 'won'
              ? null
              : `Moved to ${STAGE_LABEL[next.target]}.`,
          );
        },
        onSettled: () => flight.end(),
      },
    );
  };

  const sendApproval = (text: string, expectedVersion: number) => {
    const converted = toApproval(text, expectedVersion);
    if (!converted.ok) {
      setProblems(converted.problems);
      return;
    }
    if (!flight.begin()) return;
    reset();
    setDone(null);
    setAttempt({ kind: 'approve', reason: text });
    approve.mutate(
      { dealId: deal.id, body: converted.request },
      {
        onSuccess: () => {
          setApprovalReason('');
          setDone('Discount approved — the deal can now be won.');
        },
        onSettled: () => flight.end(),
      },
    );
  };

  const choose = (target: Stage, input: string) => {
    setProblems([]);
    setDone(null);
    if (input === 'none') {
      sendMove({ kind: 'move', target, reason: '', priceListVersion: '' }, deal.version);
    } else {
      reset();
      setAttempt(null);
      setComposing(target);
    }
  };

  const submitComposed = (event: FormEvent) => {
    event.preventDefault();
    if (composing === null || conflicted) return;
    sendMove({ kind: 'move', target: composing, reason, priceListVersion }, deal.version);
  };

  const submitApproval = (event: FormEvent) => {
    event.preventDefault();
    if (conflicted) return;
    sendApproval(approvalReason, deal.version);
  };

  const discard = () => {
    reset();
    setAttempt(null);
    setComposing(null);
    setReason('');
    setPriceListVersion('');
    setApprovalReason('');
  };

  // A re-apply is offered only if the same write still makes sense against the deal as it now is.
  const reapplicable =
    conflicted && attempt !== null
      ? attempt.kind === 'move'
        ? nextStages(deal.stage).includes(attempt.target)
        : deal.pendingApproval
      : false;

  const reapply = () => {
    if (!attempt) return;
    if (attempt.kind === 'move') sendMove(attempt, deal.version);
    else sendApproval(attempt.reason, deal.version);
  };

  if (isTerminal(deal.stage)) {
    return (
      <section
        className="lifecycle"
        aria-label="Lifecycle"
        data-testid="lifecycle"
        data-terminal="true"
      >
        <h4>Lifecycle</h4>
        <p className={`notice notice-terminal notice-${deal.stage}`} data-testid="terminal">
          <strong>{deal.stage === 'won' ? 'Won' : 'Lost'}</strong> — a final stage; no further moves.
          {deal.stage === 'lost' && deal.lostReasonCode && (
            <span className="sub">
              {' '}
              Reason: <span className="mono">{deal.lostReasonCode}</span>
            </span>
          )}
        </p>
        {done && (
          <p className="notice notice-ok" role="status">
            {done}
          </p>
        )}
      </section>
    );
  }

  const moves = offeredMoves(deal, canWrite);
  const controlsDisabled = busy || conflicted;

  return (
    <section className="lifecycle" aria-label="Lifecycle" data-testid="lifecycle">
      <h4>Lifecycle</h4>

      {deal.pendingApproval && (
        <div className="notice notice-held" role="status" data-testid="held">
          <strong>Held for lead approval.</strong>
          <p>
            The <span className="mono">{percent.format(deal.discountPct)}%</span> discount is over the
            tenant threshold, so this deal stays in negotiation until a lead with{' '}
            <span className="mono">{Permissions.DealsDiscountApprove}</span> approves it.
          </p>
        </div>
      )}

      <div className="stage-moves" role="group" aria-label="Move to stage">
        <span className="sub move-from">
          From <b>{STAGE_LABEL[deal.stage as Stage] ?? deal.stage}</b> to
        </span>
        {moves.map((move) => (
          <button
            key={move.target}
            type="button"
            className={`btn move-btn${move.target === 'lost' ? ' danger' : ''}`}
            data-target={move.target}
            data-selected={composing === move.target}
            disabled={move.blockedBy !== null || controlsDisabled}
            title={move.blockedBy ?? undefined}
            onClick={() => choose(move.target, move.input)}
          >
            {busy && attempt?.kind === 'move' && attempt.target === move.target
              ? 'Moving…'
              : STAGE_LABEL[move.target]}
          </button>
        ))}
      </div>

      {composing !== null && (
        <form className="move-form" onSubmit={submitComposed} noValidate aria-label="Move details">
          {composing === 'quoted' ? (
            <label className="field">
              <span>Price-list version to freeze</span>
              <input
                name="priceListVersion"
                className="mono"
                value={priceListVersion}
                disabled={controlsDisabled}
                onChange={(event) => setPriceListVersion(event.target.value)}
              />
            </label>
          ) : (
            <label className="field">
              <span>Lost reason code</span>
              <input
                name="lostReason"
                className="mono"
                list="lost-reason-codes"
                value={reason}
                disabled={controlsDisabled}
                onChange={(event) => setReason(event.target.value)}
              />
              <datalist id="lost-reason-codes">
                {LOST_REASON_SUGGESTIONS.map((code) => (
                  <option key={code} value={code} />
                ))}
              </datalist>
            </label>
          )}
          <div className="form-actions">
            <button
              type="submit"
              className={`btn ${composing === 'lost' ? 'danger' : 'primary'}`}
              disabled={!canWrite || controlsDisabled}
            >
              {busy ? 'Moving…' : `Move to ${STAGE_LABEL[composing]}`}
            </button>
            <button type="button" className="btn ghost" onClick={discard} disabled={busy}>
              Cancel
            </button>
          </div>
        </form>
      )}

      {deal.pendingApproval && (
        <form
          className="approve-form"
          onSubmit={submitApproval}
          noValidate
          aria-label="Approve discount"
        >
          <label className="field">
            <span>Approval reason</span>
            <input
              name="approvalReason"
              value={approvalReason}
              disabled={!canApprove || controlsDisabled}
              onChange={(event) => setApprovalReason(event.target.value)}
            />
          </label>
          <div className="form-actions">
            <button
              type="submit"
              className="btn primary"
              disabled={!canApprove || controlsDisabled}
              title={canApprove ? undefined : `Requires ${Permissions.DealsDiscountApprove}`}
            >
              {approve.isPending ? 'Approving…' : 'Approve discount'}
            </button>
          </div>
        </form>
      )}

      {problems.length > 0 && (
        <ul className="problems" role="alert">
          {problems.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      )}

      {failure?.kind === 'conflict' && attempt && (
        <div className="notice notice-conflict" role="alert" data-testid="lifecycle-conflict">
          <strong>Changed by someone else.</strong>
          <p>{failure.message}</p>
          <p className="sub">
            It is now <b>{STAGE_LABEL[deal.stage as Stage] ?? deal.stage}</b>
            <span className="mono"> (version {deal.version})</span>
            {deal.pendingApproval ? ', held for lead approval' : ''}.{' '}
            {reapplicable
              ? attempt.kind === 'move'
                ? `Your move to ${STAGE_LABEL[attempt.target]} can still be applied.`
                : 'Your approval can still be applied.'
              : attempt.kind === 'move'
                ? `Your move to ${STAGE_LABEL[attempt.target]} no longer applies from here.`
                : 'There is no longer a discount to approve.'}
          </p>
          <div className="form-actions">
            {reapplicable && (
              <button
                type="button"
                className="btn primary"
                disabled={busy || (attempt.kind === 'move' ? !canWrite : !canApprove)}
                onClick={reapply}
              >
                {attempt.kind === 'move'
                  ? `Re-apply: move to ${STAGE_LABEL[attempt.target]}`
                  : 'Re-apply approval'}
              </button>
            )}
            <button type="button" className="btn ghost" onClick={discard} disabled={busy}>
              Discard mine, keep theirs
            </button>
          </div>
        </div>
      )}

      {failure && !conflicted && (
        <p
          className={`notice notice-${failure.kind}`}
          role="alert"
          data-testid="lifecycle-failure"
        >
          {attempt?.kind === 'move' && failure.kind === 'rejected' && (
            <strong>Move to {STAGE_LABEL[attempt.target]} rejected. </strong>
          )}
          {failure.message}
        </p>
      )}

      {done && !failure && (
        <p className="notice notice-ok" role="status" data-testid="lifecycle-done">
          {done}
        </p>
      )}
    </section>
  );
}
