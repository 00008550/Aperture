import { useAccounts } from '../data/useAccounts';

/**
 * The page size the name lookup reads. It matches the create-contact panel's suggestion list, so
 * both share one cached query instead of issuing two.
 */
export const ACCOUNT_LOOKUP_LIMIT = 50;

/**
 * Resolves account ids to names for the grids that carry an `accountId` (contacts, deals). It
 * rides the existing gated `useAccounts` hook, so a caller without `accounts.read` issues no
 * accounts request at all and every id falls back to its short form. Only the loaded page is
 * consulted: an id not in it (or not visible to the caller) also falls back to the short id —
 * never a guess, and never an extra per-row fetch.
 */
export function useAccountLookup(): {
  nameOf: (id: string) => string | null;
  options: { id: string; name: string }[];
} {
  const accounts = useAccounts({ limit: ACCOUNT_LOOKUP_LIMIT });
  const options = accounts.grid.kind === 'rows' ? accounts.grid.rows : [];
  const names = new Map(options.map((account) => [account.id, account.name]));
  return { nameOf: (id) => names.get(id) ?? null, options };
}

/** An account cell: the name when it resolved, otherwise the id's first eight characters. */
export function AccountName({ id, name }: { id: string; name: string | null }) {
  return name !== null ? (
    <span title={id} data-account-resolved="true">
      {name}
    </span>
  ) : (
    <span className="mono" title={id} data-account-resolved="false">
      {id.slice(0, 8)}
    </span>
  );
}
