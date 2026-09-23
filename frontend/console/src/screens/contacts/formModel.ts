import { ApiError, type CreateContactRequest } from '../../api';

/**
 * The contact form's model, kept pure so its rules are tested without rendering — the same shape
 * as the accounts form model. The draft holds exactly what the inputs hold; conversion happens
 * once, here, and a draft that cannot convert yields problems instead of a request.
 *
 * The parent account is not part of the request body: it is the route parameter of
 * `POST /api/accounts/{accountId}/contacts`, and the server validates it is in the caller's scope.
 * The client only checks it is shaped like an id — whether it is *visible* is the server's call.
 */
export interface ContactDraft {
  accountId: string;
  name: string;
  email: string;
  phone: string;
  messenger: string;
}

export const EMPTY_CONTACT_DRAFT: ContactDraft = {
  accountId: '',
  name: '',
  email: '',
  phone: '',
  messenger: '',
};

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

type Converted =
  | { ok: true; accountId: string; request: CreateContactRequest }
  | { ok: false; problems: string[] };

export function toCreateContact(draft: ContactDraft): Converted {
  const problems: string[] = [];
  const accountId = draft.accountId.trim();
  if (!accountId) problems.push('Account is required — a contact always belongs to one.');
  else if (!GUID.test(accountId)) problems.push('Account must be an account id (a GUID).');

  const name = draft.name.trim();
  if (!name) problems.push('Name is required.');

  // Empty optional fields are null — "not given", never an empty string.
  const email = draft.email.trim() || null;
  if (email !== null && !email.includes('@')) problems.push('Email must look like an address.');

  if (problems.length > 0) return { ok: false, problems };
  return {
    ok: true,
    accountId,
    request: {
      name,
      email,
      phone: draft.phone.trim() || null,
      messenger: draft.messenger.trim() || null,
    },
  };
}

export type ContactWriteFailure =
  | { kind: 'rejected'; message: string }
  | { kind: 'gone'; message: string }
  | { kind: 'denied'; message: string }
  | { kind: 'failed'; message: string };

/**
 * What a failed contact write means. A 404 means different things per write: on create it is the
 * *parent account* that is unknown or outside the caller's scope (the server says so in its body,
 * and that wording is shown); on depart it is the contact itself that is no longer visible.
 */
export function describeContactError(
  error: unknown,
  mode: 'create' | 'depart',
): ContactWriteFailure {
  if (!(error instanceof ApiError)) {
    return { kind: 'failed', message: 'The API could not be reached. Nothing was saved.' };
  }
  const server = error.serverMessage;
  switch (error.status) {
    case 404:
      return mode === 'create'
        ? { kind: 'gone', message: server ?? 'No account with this id is visible to you.' }
        : { kind: 'gone', message: 'This contact is no longer visible to you.' };
    case 400:
    case 409:
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
