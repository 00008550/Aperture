import { ApiError, type AddDealLineRequest, type CreateDealRequest } from '../../api';

/**
 * The deal forms' model, kept pure so the rules are tested without rendering — the same shape as
 * the accounts and contacts form models. Drafts hold exactly what the inputs hold (strings);
 * conversion happens once, here, and a draft that cannot convert yields problems, not a request.
 *
 * The client checks only shape — the domain's own guards (non-negative money, a 0–100 discount,
 * a positive quantity) are mirrored so an obviously bad value never leaves the browser, but the
 * server remains the authority and its answer is shown in its own words.
 */

export interface DealDraft {
  accountId: string;
  name: string;
  amount: string;
  discountPct: string;
}

export const EMPTY_DEAL_DRAFT: DealDraft = { accountId: '', name: '', amount: '', discountPct: '0' };

export interface LineDraft {
  productRef: string;
  unitPrice: string;
  quantity: string;
  priceListVersion: string;
}

export const EMPTY_LINE_DRAFT: LineDraft = {
  productRef: '',
  unitPrice: '',
  quantity: '1',
  priceListVersion: '',
};

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const DECIMAL = /^\d+(\.\d+)?$/;
const INTEGER = /^\d+$/;

type Converted<T> = { ok: true; request: T } | { ok: false; problems: string[] };

export function toCreateDeal(draft: DealDraft): Converted<CreateDealRequest> {
  const problems: string[] = [];
  const accountId = draft.accountId.trim();
  if (!accountId) problems.push('Account is required — a deal always belongs to one.');
  else if (!GUID.test(accountId)) problems.push('Account must be an account id (a GUID).');

  const name = draft.name.trim();
  if (!name) problems.push('Name is required.');

  const amount = draft.amount.trim();
  if (!DECIMAL.test(amount)) problems.push('Amount must be a non-negative number.');

  const discount = draft.discountPct.trim();
  if (!DECIMAL.test(discount) || Number(discount) > 100) {
    problems.push('Discount must be a percentage between 0 and 100.');
  }

  if (problems.length > 0) return { ok: false, problems };
  return {
    ok: true,
    request: { accountId, name, amount: Number(amount), discountPct: Number(discount) },
  };
}

export function toAddLine(draft: LineDraft): Converted<AddDealLineRequest> {
  const problems: string[] = [];
  const productRef = draft.productRef.trim();
  if (!productRef) problems.push('Product is required.');

  const unitPrice = draft.unitPrice.trim();
  if (!DECIMAL.test(unitPrice)) problems.push('Unit price must be a non-negative number.');

  const quantity = draft.quantity.trim();
  if (!INTEGER.test(quantity) || Number(quantity) <= 0) {
    problems.push('Quantity must be a whole number greater than zero.');
  }

  if (problems.length > 0) return { ok: false, problems };
  return {
    ok: true,
    request: {
      productRef,
      unitPrice: Number(unitPrice),
      quantity: Number(quantity),
      // Blank means "not priced against a list" — null, never an empty string.
      priceListVersion: draft.priceListVersion.trim() || null,
    },
  };
}

export type DealWriteFailure =
  | { kind: 'rejected'; message: string }
  | { kind: 'gone'; message: string }
  | { kind: 'denied'; message: string }
  | { kind: 'failed'; message: string };

/**
 * What a failed deal write means. A 404 names a different thing per write: on create it is the
 * *parent account* (the server says so in its body); on add-line it is the deal itself, which the
 * server answers with no body. 400/409/422 are the server's refusals, shown in its words.
 */
export function describeDealError(error: unknown, mode: 'create' | 'add-line'): DealWriteFailure {
  if (!(error instanceof ApiError)) {
    return { kind: 'failed', message: 'The API could not be reached. Nothing was saved.' };
  }
  const server = error.serverMessage;
  switch (error.status) {
    case 404:
      return mode === 'create'
        ? { kind: 'gone', message: server ?? 'No account with this id is visible to you.' }
        : { kind: 'gone', message: server ?? 'This deal is no longer visible to you.' };
    case 409:
      if (mode === 'add-line') {
        return {
          kind: 'rejected',
          message: 'This deal changed since you loaded it. Review the current deal and add the line again.',
        };
      }
      return { kind: 'rejected', message: server ?? `The API rejected this (${error.status}).` };
    case 400:
    case 422:
      return { kind: 'rejected', message: server ?? `The API rejected this (${error.status}).` };
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

/** A line's total, in the same unit as its price. */
export const lineTotal = (line: { unitPrice: number; quantity: number }) =>
  line.unitPrice * line.quantity;

export const money = new Intl.NumberFormat(undefined, {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
export const percent = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 });
export const date = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
});
