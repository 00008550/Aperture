import { ApiError, type Page } from '../api';
import type { Permission } from '../permissions';
import { useSession } from '../useSession';

/**
 * The `can()` fail-closed gate every Sales hook consults before it touches the network. A read
 * is enabled only when there is a token AND the resolved session grants the permission — an
 * unresolved session (loading, errored) is "no", never "maybe". The server denies anyway; this
 * keeps the console from issuing requests it already knows are refused.
 */
export function useGate(permission: Permission) {
  const session = useSession();
  return { session, allowed: session.can(permission) };
}

/** Thrown locally, before any fetch, when a write is attempted without its permission. */
export function deniedLocally(permission: Permission): ApiError {
  return new ApiError(403, `Not permitted: ${permission}`);
}

/**
 * What a Sales grid is showing — the model the screens (P4+) render from, so the "nothing is
 * visible to you" surface is decided once, here, and not re-derived per screen.
 *
 * Fail closed, in order: no permission is `denied`; a session with zero scopes is `no-scope`
 * (edge 5 — the stated "nothing is visible" surface, never an empty table and never "all rows");
 * then loading; then `empty` vs `rows` on what the server actually returned.
 */
export type GridModel<T> =
  | { kind: 'denied' }
  | { kind: 'no-scope' }
  | { kind: 'loading' }
  | { kind: 'error'; error: unknown }
  | { kind: 'empty' }
  | { kind: 'rows'; rows: T[]; hasMore: boolean };

export interface GridInput<T> {
  allowed: boolean;
  scopeCount: number | undefined;
  pages: Page<T>[] | undefined;
  isPending: boolean;
  error: unknown;
  hasNextPage: boolean;
}

export function toGridModel<T>(input: GridInput<T>): GridModel<T> {
  if (!input.allowed) return { kind: 'denied' };
  // An unresolved session is not "some scopes": it cannot be judged, so it is still loading.
  if (input.scopeCount === undefined) return { kind: 'loading' };
  if (input.scopeCount === 0) return { kind: 'no-scope' };
  if (input.error) return { kind: 'error', error: input.error };
  if (input.isPending || !input.pages) return { kind: 'loading' };
  const rows = input.pages.flatMap((page) => page.items);
  if (rows.length === 0) return { kind: 'empty' };
  return { kind: 'rows', rows, hasMore: input.hasNextPage };
}
