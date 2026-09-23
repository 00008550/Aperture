import { clearAccessToken, getAccessToken } from './auth';

/**
 * A non-2xx answer from the API. It keeps the status AND the parsed response body: the server is
 * the authority on *why* a write was refused (a stale `xmin`, a duplicate tax id, an illegal
 * lifecycle move), and a screen that can only say "409" has thrown that answer away.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    /** The parsed JSON body, the raw text when it was not JSON, or `null` when there was none. */
    readonly body: unknown = null,
  ) {
    super(message);
  }

  /** The server's own human-readable reason, when the body carries one. */
  get serverMessage(): string | null {
    return serverMessageOf(this.body);
  }
}

/**
 * Pulls the human-readable reason out of an error body. The Sales endpoints answer
 * `{ error: "…" }`; framework failures (model binding, `Results.Problem`) answer RFC 7807
 * ProblemDetails, whose `detail` is more specific than its `title`, and whose validation variant
 * lists per-field `errors`. Anything else yields `null` — the caller falls back to its own wording
 * rather than showing a guess.
 */
export function serverMessageOf(body: unknown): string | null {
  if (typeof body === 'string') return body.trim() || null;
  if (typeof body !== 'object' || body === null) return null;
  const record = body as Record<string, unknown>;
  for (const key of ['error', 'detail', 'message'] as const) {
    const value = record[key];
    if (typeof value === 'string' && value.trim()) return value;
  }
  const errors = record.errors;
  if (typeof errors === 'object' && errors !== null) {
    const messages = Object.values(errors as Record<string, unknown>)
      .flatMap((value) => (Array.isArray(value) ? value : [value]))
      .filter((value): value is string => typeof value === 'string' && value.trim() !== '');
    if (messages.length > 0) return messages.join(' ');
  }
  const title = record.title;
  return typeof title === 'string' && title.trim() ? title : null;
}

/** Reads an error response's body once: JSON when it parses, text otherwise, `null` when empty. */
async function readErrorBody(res: Response): Promise<unknown> {
  let text: string;
  try {
    text = await res.text();
  } catch {
    return null;
  }
  if (!text) return null;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return text;
  }
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const token = getAccessToken();

  const res = await fetch(path, {
    ...init,
    headers: {
      'content-type': 'application/json',
      // No token, no Authorization header — the request goes out anonymous and the API
      // answers 401. Sending an empty bearer would be indistinguishable from a malformed one.
      ...(token ? { authorization: `Bearer ${token}` } : {}),
      ...(init?.headers ?? {}),
    },
  });

  if (!res.ok) {
    throw new ApiError(
      res.status,
      `${init?.method ?? 'GET'} ${path} -> ${res.status}`,
      await readErrorBody(res),
    );
  }
  return (await res.json()) as T;
}

/**
 * The message shown at the sign-in surface when the API refuses the token. Kept here — the one
 * place a rejected credential is discarded — so `useSession` and every data hook drop the token
 * the same way and say the same thing, rather than each forking its own copy of the rule.
 */
export const TOKEN_REFUSED_MESSAGE =
  'That token was refused. It may have expired, or the account may no longer be an ' +
  'active member of the tenant it names.';

/** A 401 or 403: the API refused the credential itself, not merely this request's shape. */
export function isAuthRejection(error: unknown): error is ApiError {
  return error instanceof ApiError && (error.status === 401 || error.status === 403);
}

/**
 * The single fail-closed API path for authenticated data: a `401`/`403` is not a transient
 * error to retry — it means the token is no good, so drop it (returning the whole console to
 * sign-in) exactly as `useSession` does, then rethrow so the caller's query still lands in its
 * error state. Every read and write hook goes through here so there is one, and only one, place
 * that decides a token has died.
 */
export async function apiAuthed<T>(path: string, init?: RequestInit): Promise<T> {
  try {
    return await api<T>(path, init);
  } catch (error) {
    if (isAuthRejection(error)) clearAccessToken(TOKEN_REFUSED_MESSAGE);
    throw error;
  }
}

/**
 * Builds a `?a=1&b=2` query string, dropping `undefined`/`null` so an absent cursor or limit
 * never becomes the literal string "undefined". Returns "" (not "?") when nothing is set.
 */
export function toQueryString(
  params: Record<string, string | number | boolean | null | undefined>,
): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== null && value !== undefined) search.set(key, String(value));
  }
  const query = search.toString();
  return query ? `?${query}` : '';
}

/** One data scope, in the shape `GET /api/me` returns it (`ScopeResponse` in MeEndpoints.cs). */
export interface SessionScope {
  kind: string;
  targetId: string | null;
}

/** The `MeResponse` contract. Kept field-for-field — the API is the source of truth. */
export interface Session {
  tenantId: string;
  userId: string;
  email: string;
  displayName: string;
  permissions: string[];
  scopes: SessionScope[];
}

// ---------------------------------------------------------------------------
// Sales contracts (002). Hand-typed, field-for-field against the server's records
// (AccountModels.cs / ContactModels.cs / DealModels.cs) — the server is the source of truth,
// and this stays hand-kept only until an OpenAPI document is published (see permissions.ts).
// `version`/`expectedVersion` are the row's PostgreSQL `xmin` (a `uint`), serialized as a JSON
// number; a write round-trips the value it read so a concurrent edit loses (409) not clobbers.
// ---------------------------------------------------------------------------

/** One keyset page: the rows, and the cursor that fetches the next page — `null` at the end. */
export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

// --- Accounts (AccountModels.cs) ---

export interface AccountView {
  id: string;
  tenantId: string;
  ownerUserId: string;
  name: string;
  taxId: string;
  creditLimit: number;
  paymentTermsDays: number;
  regionId: string | null;
  teamId: string | null;
  accountId: string;
  createdAt: string;
  version: number;
}

export interface CreateAccountRequest {
  name: string;
  taxId: string;
  creditLimit: number;
  paymentTermsDays: number;
  regionId: string | null;
  teamId: string | null;
}

export interface UpdateAccountRequest {
  ownerUserId: string;
  name: string;
  creditLimit: number;
  paymentTermsDays: number;
  regionId: string | null;
  teamId: string | null;
  expectedVersion: number;
}

// --- Contacts (ContactModels.cs) ---

export interface ContactView {
  id: string;
  tenantId: string;
  accountId: string;
  ownerUserId: string;
  teamId: string | null;
  regionId: string | null;
  name: string;
  email: string | null;
  phone: string | null;
  messenger: string | null;
  isDeparted: boolean;
  departedAt: string | null;
  createdAt: string;
}

export interface CreateContactRequest {
  name: string;
  email: string | null;
  phone: string | null;
  messenger: string | null;
}

// --- Deals (DealModels.cs) ---

export interface DealLineView {
  id: string;
  dealId: string;
  productRef: string;
  unitPrice: number;
  quantity: number;
  priceListVersion: string | null;
}

export interface DealView {
  id: string;
  tenantId: string;
  accountId: string;
  ownerUserId: string;
  teamId: string | null;
  regionId: string | null;
  name: string;
  stage: string;
  amount: number;
  discountPct: number;
  frozenPriceListVersion: string | null;
  pendingApproval: boolean;
  lostReasonCode: string | null;
  createdAt: string;
  version: number;
  // The grid returns deals without lines (an empty list); a single-deal read includes them.
  lines: DealLineView[];
}

export interface CreateDealRequest {
  accountId: string;
  name: string;
  amount: number;
  discountPct: number;
}

export interface AddDealLineRequest {
  productRef: string;
  unitPrice: number;
  quantity: number;
  priceListVersion: string | null;
}

/** How many rows to request in one keyset page, and where to resume. `cursor` null = first page. */
export interface PageParams {
  limit?: number | undefined;
  cursor?: string | null | undefined;
}

// --- Endpoint functions: one thin call per route, typed to its contract. ---

export function listAccounts(params: PageParams): Promise<Page<AccountView>> {
  return apiAuthed<Page<AccountView>>(
    `/api/accounts${toQueryString({ limit: params.limit, cursor: params.cursor })}`,
  );
}

export function getAccount(id: string): Promise<AccountView> {
  return apiAuthed<AccountView>(`/api/accounts/${id}`);
}

export function createAccount(body: CreateAccountRequest): Promise<AccountView> {
  return apiAuthed<AccountView>('/api/accounts', {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function updateAccount(id: string, body: UpdateAccountRequest): Promise<AccountView> {
  return apiAuthed<AccountView>(`/api/accounts/${id}`, {
    method: 'PATCH',
    body: JSON.stringify(body),
  });
}

export interface ListContactsParams extends PageParams {
  includeDeparted?: boolean | undefined;
}

export function listContacts(params: ListContactsParams): Promise<Page<ContactView>> {
  return apiAuthed<Page<ContactView>>(
    `/api/contacts${toQueryString({
      includeDeparted: params.includeDeparted,
      limit: params.limit,
      cursor: params.cursor,
    })}`,
  );
}

export function createContact(
  accountId: string,
  body: CreateContactRequest,
): Promise<ContactView> {
  return apiAuthed<ContactView>(`/api/accounts/${accountId}/contacts`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function departContact(id: string): Promise<ContactView> {
  return apiAuthed<ContactView>(`/api/contacts/${id}/depart`, { method: 'POST' });
}

export function listDeals(params: PageParams): Promise<Page<DealView>> {
  return apiAuthed<Page<DealView>>(
    `/api/deals${toQueryString({ limit: params.limit, cursor: params.cursor })}`,
  );
}

export function getDeal(id: string): Promise<DealView> {
  return apiAuthed<DealView>(`/api/deals/${id}`);
}

export function createDeal(body: CreateDealRequest): Promise<DealView> {
  return apiAuthed<DealView>('/api/deals', {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function addDealLine(dealId: string, body: AddDealLineRequest): Promise<DealView> {
  return apiAuthed<DealView>(`/api/deals/${dealId}/lines`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/**
 * A lifecycle move (TransitionDealRequest in DealModels.cs). `reason` is required only for
 * `lost` (rule 4), `priceListVersion` only for `quoted` (rule 2); `expectedVersion` is the xmin
 * last read — a stale value is a 409 carrying the current deal. A held discount comes back 200
 * with `pendingApproval: true` and the stage unchanged.
 */
export interface TransitionDealRequest {
  targetStage: string;
  reason?: string | null;
  priceListVersion?: string | null;
  expectedVersion?: number | null;
}

/** Clears a pending discount approval (ApproveDiscountRequest in DealModels.cs). */
export interface ApproveDiscountRequest {
  reason: string;
  expectedVersion?: number | null;
}

export function transitionDeal(dealId: string, body: TransitionDealRequest): Promise<DealView> {
  return apiAuthed<DealView>(`/api/deals/${dealId}/transition`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function approveDealDiscount(
  dealId: string,
  body: ApproveDiscountRequest,
): Promise<DealView> {
  return apiAuthed<DealView>(`/api/deals/${dealId}/approve-discount`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}
