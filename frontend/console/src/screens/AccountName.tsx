/**
 * An account cell for the rows that carry an `accountId` (contacts, deals). The name comes from the
 * row itself — the server resolves it under the caller's scope (011-P4) — so no grid issues an
 * accounts request just to label rows. `null` means the account is not visible to the caller: the
 * cell falls back to the id's first eight characters, never a guess.
 */
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
