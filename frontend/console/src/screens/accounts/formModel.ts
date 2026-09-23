import {
  ApiError,
  type AccountView,
  type CreateAccountRequest,
  type UpdateAccountRequest,
} from '../../api';

/**
 * The account form's model, kept pure so its rules are tested without rendering. The draft holds
 * exactly what the inputs hold (strings); conversion to a request happens once, here, and a draft
 * that cannot convert yields problems instead of a request — the form never sends a guess.
 *
 * Client checks are a courtesy that saves a round-trip. The server validates regardless and its
 * answer wins: whatever it says is shown verbatim (`describeWriteError`).
 */
export interface AccountDraft {
  name: string;
  taxId: string;
  creditLimit: string;
  paymentTermsDays: string;
  regionId: string;
  teamId: string;
}

export const EMPTY_DRAFT: AccountDraft = {
  name: '',
  taxId: '',
  creditLimit: '0',
  paymentTermsDays: '30',
  regionId: '',
  teamId: '',
};

export function draftFromAccount(account: AccountView): AccountDraft {
  return {
    name: account.name,
    taxId: account.taxId,
    creditLimit: String(account.creditLimit),
    paymentTermsDays: String(account.paymentTermsDays),
    regionId: account.regionId ?? '',
    teamId: account.teamId ?? '',
  };
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

type Converted<T> = { ok: true; request: T } | { ok: false; problems: string[] };

interface CommonFields {
  name: string;
  creditLimit: number;
  paymentTermsDays: number;
  regionId: string | null;
  teamId: string | null;
}

function convertCommon(draft: AccountDraft): Converted<CommonFields> {
  const problems: string[] = [];
  const name = draft.name.trim();
  if (!name) problems.push('Name is required.');

  const creditLimit = Number(draft.creditLimit.trim());
  if (draft.creditLimit.trim() === '' || !Number.isFinite(creditLimit) || creditLimit < 0) {
    problems.push('Credit limit must be a number of zero or more.');
  }

  const terms = Number(draft.paymentTermsDays.trim());
  if (draft.paymentTermsDays.trim() === '' || !Number.isInteger(terms) || terms < 0) {
    problems.push('Payment terms must be a whole number of days, zero or more.');
  }

  // An empty id field means "none" (null) — never an empty string the server would reject as a
  // malformed GUID, and never a guessed default.
  const regionId = draft.regionId.trim() || null;
  const teamId = draft.teamId.trim() || null;
  if (regionId !== null && !GUID.test(regionId)) problems.push('Region must be a GUID or empty.');
  if (teamId !== null && !GUID.test(teamId)) problems.push('Team must be a GUID or empty.');

  if (problems.length > 0) return { ok: false, problems };
  return {
    ok: true,
    request: { name, creditLimit, paymentTermsDays: terms, regionId, teamId },
  };
}

export function toCreateRequest(draft: AccountDraft): Converted<CreateAccountRequest> {
  const common = convertCommon(draft);
  const taxId = draft.taxId.trim();
  const problems = common.ok ? [] : [...common.problems];
  if (!taxId) problems.unshift('Tax ID is required.');
  if (!common.ok || problems.length > 0) return { ok: false, problems };
  return { ok: true, request: { ...common.request, taxId } };
}

/**
 * An edit. `expectedVersion` is the `xmin` of the copy the user was looking at — round-tripped,
 * never refreshed behind their back, so a concurrent edit becomes a 409 and not a lost update.
 * The owner is carried through unchanged: reassignment is a deliberate act this form does not offer.
 */
export function toUpdateRequest(
  draft: AccountDraft,
  ownerUserId: string,
  expectedVersion: number,
): Converted<UpdateAccountRequest> {
  const common = convertCommon(draft);
  if (!common.ok) return common;
  return { ok: true, request: { ...common.request, ownerUserId, expectedVersion } };
}

export type WriteFailure =
  | { kind: 'conflict'; message: string }
  | { kind: 'rejected'; message: string }
  | { kind: 'gone'; message: string }
  | { kind: 'denied'; message: string }
  | { kind: 'failed'; message: string };

/**
 * What a failed write means, in the user's terms. The mode matters because the server uses 409
 * for two different things: on create it is a duplicate tax id (the user's input is the problem),
 * on edit it is a stale version (somebody else moved first). The server's own message is
 * preferred whenever it sent one.
 */
export function describeWriteError(error: unknown, mode: 'create' | 'edit'): WriteFailure {
  if (!(error instanceof ApiError)) {
    return { kind: 'failed', message: 'The API could not be reached. Nothing was saved.' };
  }
  const server = error.serverMessage;
  switch (error.status) {
    case 409:
      return mode === 'edit'
        ? {
            kind: 'conflict',
            message: server ?? 'The account was modified by someone else.',
          }
        : { kind: 'rejected', message: server ?? 'An account with this tax identifier exists.' };
    case 400:
    case 422:
      return { kind: 'rejected', message: server ?? `The API rejected this (${error.status}).` };
    case 404:
      return { kind: 'gone', message: 'This account is no longer visible to you.' };
    case 401:
    case 403:
      return { kind: 'denied', message: server ?? 'You are not permitted to make this change.' };
    default:
      return {
        kind: 'failed',
        message: server ?? `The API could not complete this (${error.status}). Nothing was saved.`,
      };
  }
}

/** Fields whose server value now differs from the user's draft — shown after a 409. */
export function changedFields(draft: AccountDraft, latest: AccountView): (keyof AccountDraft)[] {
  const server = draftFromAccount(latest);
  return (Object.keys(server) as (keyof AccountDraft)[]).filter(
    (key) => server[key].trim() !== draft[key].trim(),
  );
}
