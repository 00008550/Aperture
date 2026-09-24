import {
  ApiError,
  type ApproveDiscountRequest,
  type DealView,
  type TransitionDealRequest,
} from '../../../api';
import { dealInConflict } from '../../../data/useDeals';

/**
 * The lifecycle control's model, kept pure so its rules are tested without rendering.
 *
 * The server's table (`DealStateMachine.cs`) is the authority. This mirror exists only so the
 * console *offers* the moves that can be legal from where the deal is — it never decides that a
 * move succeeded. Every answer (the moved deal, a held discount, a 422, a 409) is rendered from
 * what the server returned.
 */

export type Stage = 'new' | 'qualified' | 'quoted' | 'negotiation' | 'won' | 'lost';

/** The legal edges, `from → to[]`. `won` and `lost` are terminal: nothing leaves them. */
const NEXT: Record<Stage, readonly Stage[]> = {
  new: ['qualified'],
  qualified: ['quoted'],
  quoted: ['negotiation'],
  negotiation: ['won', 'lost'],
  won: [],
  lost: [],
};

const isStage = (value: string): value is Stage => Object.hasOwn(NEXT, value);

/**
 * The next stages offered from `stage`. An unknown stage (a server newer than this console) offers
 * nothing — fail closed, never "everything".
 */
export function nextStages(stage: string): readonly Stage[] {
  return isStage(stage) ? NEXT[stage] : [];
}

export const isTerminal = (stage: string) => stage === 'won' || stage === 'lost';

/** What a move needs from the user before it can be sent. */
export type MoveInput = 'none' | 'price-list-version' | 'reason';

export const inputFor = (target: Stage): MoveInput =>
  target === 'quoted' ? 'price-list-version' : target === 'lost' ? 'reason' : 'none';

export interface OfferedMove {
  target: Stage;
  input: MoveInput;
  /** Why the move is not offered right now, or null when it may be attempted. */
  blockedBy: string | null;
}

/**
 * The moves shown for a deal. A deal held for discount approval cannot be won again on the
 * agent's say-so — retrying would only re-hold it — so `won` is blocked until a lead approves;
 * `lost` stays open (the server permits it). Without `deals.write` every move is blocked.
 */
export function offeredMoves(deal: DealView, canWrite: boolean): OfferedMove[] {
  return nextStages(deal.stage).map((target) => ({
    target,
    input: inputFor(target),
    blockedBy: !canWrite
      ? 'Requires deals.write'
      : target === 'won' && deal.pendingApproval
        ? 'Held for lead approval — a lead must approve the discount first'
        : null,
  }));
}

type Converted<T> = { ok: true; request: T } | { ok: false; problems: string[] };

/**
 * Builds the transition request, always carrying the version the move was based on. Rule 4 (lost
 * needs a reason code) and rule 2 (quoted needs a price-list version) are checked here so an
 * obviously incomplete move never leaves the browser — the server checks them again regardless.
 */
export function toTransition(
  target: Stage,
  expectedVersion: number,
  values: { reason?: string; priceListVersion?: string } = {},
): Converted<TransitionDealRequest> {
  const reason = values.reason?.trim() ?? '';
  const priceListVersion = values.priceListVersion?.trim() ?? '';
  if (target === 'lost' && !reason) {
    return { ok: false, problems: ['A lost deal needs a reason code.'] };
  }
  if (target === 'quoted' && !priceListVersion) {
    return { ok: false, problems: ['Quoting freezes a price-list version — name the version.'] };
  }
  return {
    ok: true,
    request: {
      targetStage: target,
      reason: target === 'lost' ? reason : null,
      priceListVersion: target === 'quoted' ? priceListVersion : null,
      expectedVersion,
    },
  };
}

/** Builds the approval request. The reason is the audited *why*; without it nothing is sent. */
export function toApproval(reason: string, expectedVersion: number): Converted<ApproveDiscountRequest> {
  const trimmed = reason.trim();
  if (!trimmed) return { ok: false, problems: ['An approval needs a reason — it is audited.'] };
  return { ok: true, request: { reason: trimmed, expectedVersion } };
}

export type LifecycleFailure =
  | { kind: 'conflict'; message: string; current: DealView }
  | { kind: 'rejected'; message: string }
  | { kind: 'denied'; message: string }
  | { kind: 'gone'; message: string }
  | { kind: 'failed'; message: string };

/**
 * What a failed lifecycle write means. A 409 carrying a deal is a version race (the conflict flow);
 * a 409 without one, a 400 and a 422 are the server's refusals, shown in its words; a 403 is "not
 * permitted" — the user stays signed in.
 */
export function describeLifecycleError(error: unknown): LifecycleFailure {
  if (!(error instanceof ApiError)) {
    return { kind: 'failed', message: 'The API could not be reached. Nothing was changed.' };
  }
  const current = dealInConflict(error);
  if (current) {
    return {
      kind: 'conflict',
      message: 'This deal was changed by someone else before your move landed.',
      current,
    };
  }
  const server = error.serverMessage;
  switch (error.status) {
    case 400:
    case 409:
    case 422:
      return { kind: 'rejected', message: server ?? `The API rejected this move (${error.status}).` };
    case 403:
      return { kind: 'denied', message: 'You are not permitted to do this.' };
    case 404:
      return { kind: 'gone', message: 'This deal is no longer visible to you.' };
    default:
      return {
        kind: 'failed',
        message: server ?? `The API could not complete this (${error.status}). Nothing was changed.`,
      };
  }
}

/** Common lost-reason codes, offered as suggestions — the field is free text. */
export const LOST_REASON_SUGGESTIONS = [
  'price',
  'competitor',
  'no-budget',
  'no-decision',
  'timing',
  'requirements-fit',
] as const;

export const STAGE_LABEL: Record<Stage, string> = {
  new: 'New',
  qualified: 'Qualified',
  quoted: 'Quoted',
  negotiation: 'Negotiation',
  won: 'Won',
  lost: 'Lost',
};
