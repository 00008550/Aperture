import { useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { useAccounts } from '../../data/useAccounts';
import { Permissions } from '../../permissions';
import { useSession } from '../../useSession';
import { AccountsGrid } from './AccountsGrid';
import { CreateAccountPanel, EditAccountPanel } from './AccountForms';
import { GuardedButton } from '../../a11y/GuardedButton';

/** Rows per keyset page. */
export const ACCOUNTS_PAGE_SIZE = 25;

/**
 * The Accounts screen: the grid floating over the field, with a glass side panel for create and
 * edit. The route guard has already asked `can('accounts.read')` before this mounts; write controls
 * ask `can('accounts.write')` the same fail-closed way and are disabled — never hidden — without it.
 * The selected account lives in the URL (`/accounts/:accountId`), so a selection is linkable and
 * survives a reload, and no second store mirrors it.
 */
export function AccountsScreen() {
  const { accountId } = useParams();
  const navigate = useNavigate();
  const { can } = useSession();
  const canWrite = can(Permissions.AccountsWrite);
  const [creating, setCreating] = useState(false);
  const accounts = useAccounts({ limit: ACCOUNTS_PAGE_SIZE });

  const selectedId = creating ? null : (accountId ?? null);
  const panelOpen = creating || selectedId !== null;

  return (
    <div className="screen">
      <header className="screen-head">
        <div>
          <h1>Accounts</h1>
          <p className="sub">
            Customers in your scopes, from <span className="mono">GET /api/accounts</span>.
          </p>
        </div>
        <GuardedButton
          type="button"
          className="btn primary"
          disabled={!canWrite}
          deniedReason={canWrite ? null : `Requires ${Permissions.AccountsWrite}`}
          onClick={() => {
            setCreating(true);
            if (accountId) navigate('/accounts');
          }}
        >
          New account
        </GuardedButton>
      </header>

      <div className="screen-body" data-panel-open={panelOpen}>
        <AccountsGrid
          grid={accounts.grid}
          selectedId={selectedId}
          onSelect={(id) => {
            setCreating(false);
            navigate(`/accounts/${id}`);
          }}
          onLoadMore={() => void accounts.fetchNextPage()}
          loadingMore={accounts.isFetchingNextPage}
          onRetry={() => void accounts.refetch()}
        />

        {creating && (
          <CreateAccountPanel
            canWrite={canWrite}
            onClose={() => setCreating(false)}
            onCreated={(account) => {
              setCreating(false);
              navigate(`/accounts/${account.id}`);
            }}
          />
        )}

        {selectedId !== null && (
          // Keyed on the id: switching rows starts a fresh draft, never carries one account's
          // unsaved edits (or its version) onto another.
          <EditAccountPanel
            key={selectedId}
            id={selectedId}
            canWrite={canWrite}
            onClose={() => navigate('/accounts')}
          />
        )}
      </div>
    </div>
  );
}
