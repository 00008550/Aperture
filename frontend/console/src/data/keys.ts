import type { ListContactsParams, PageParams } from '../api';

/**
 * The one place query keys are minted, so a key is never spelled two subtly-different ways in
 * two files (the classic "the mutation invalidated `['account']` but the query is `['accounts']`"
 * bug). Every key opens with its resource namespace, so a mutation can invalidate a whole
 * resource with the namespace prefix (`queryKeys.accounts.all`) and TanStack Query's prefix match
 * catches every list, page and detail under it.
 *
 * The token is folded into each key after the namespace. Signing in as somebody else changes the
 * token, so their grids cannot be answered out of the previous user's cache entry — the same
 * fail-closed keying `useSession` uses for `['session', token]`, applied to every Sales read. It
 * sits after the namespace, not before it, precisely so the prefix-invalidation above still works.
 */
export const queryKeys = {
  accounts: {
    all: ['accounts'] as const,
    list: (token: string | null, params: PageParams) =>
      ['accounts', 'list', token, params] as const,
    detail: (token: string | null, id: string) => ['accounts', 'detail', token, id] as const,
  },
  contacts: {
    all: ['contacts'] as const,
    list: (token: string | null, params: ListContactsParams) =>
      ['contacts', 'list', token, params] as const,
  },
  deals: {
    all: ['deals'] as const,
    list: (token: string | null, params: PageParams) => ['deals', 'list', token, params] as const,
    detail: (token: string | null, id: string) => ['deals', 'detail', token, id] as const,
  },
} as const;
