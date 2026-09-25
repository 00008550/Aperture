import { useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { useDeals } from '../../data/useDeals';
import { Permissions } from '../../permissions';
import { useSession } from '../../useSession';
import { CreateDealPanel, DealDetailPanel } from './DealPanels';
import { DealsGrid } from './DealsGrid';
import { GuardedButton } from '../../a11y/GuardedButton';

/** Rows per keyset page. */
export const DEALS_PAGE_SIZE = 25;

/**
 * The Deals screen: the grid over the field, with a glass side panel for the selected deal (its
 * lines, and add-line) or for a new deal. The route guard has already asked `can('deals.read')`
 * before this mounts; both writes ask `can('deals.write')` the same fail-closed way and are
 * disabled — never hidden — without it. The selected deal lives in the URL (`/deals/:dealId`),
 * as on Accounts, so a selection is linkable and survives a reload.
 *
 * The detail carries the lifecycle control (010-P7): moves ask `can('deals.write')`, discount
 * approval asks `can('deals.discount.approve')`, each disabled — never hidden — without it.
 */
export function DealsScreen() {
  const { dealId } = useParams();
  const navigate = useNavigate();
  const { can } = useSession();
  const canWrite = can(Permissions.DealsWrite);
  const canApprove = can(Permissions.DealsDiscountApprove);
  const [creating, setCreating] = useState(false);
  const deals = useDeals({ limit: DEALS_PAGE_SIZE });

  const selectedId = creating ? null : (dealId ?? null);
  const panelOpen = creating || selectedId !== null;
  const selectedAccount =
    deals.grid.kind === 'rows'
      ? (deals.grid.rows.find((deal) => deal.id === dealId)?.accountId ?? '')
      : '';

  return (
    <div className="screen">
      <header className="screen-head">
        <div>
          <h1>Deals</h1>
          <p className="sub">
            Opportunities in your scopes, from <span className="mono">GET /api/deals</span>.
          </p>
        </div>
        <GuardedButton
          type="button"
          className="btn primary"
          disabled={!canWrite}
          deniedReason={canWrite ? null : `Requires ${Permissions.DealsWrite}`}
          onClick={() => {
            setCreating(true);
            if (dealId) navigate('/deals');
          }}
        >
          New deal
        </GuardedButton>
      </header>

      <div className="screen-body" data-panel-open={panelOpen}>
        <DealsGrid
          grid={deals.grid}
          selectedId={selectedId}
          onSelect={(id) => {
            setCreating(false);
            navigate(`/deals/${id}`);
          }}
          onLoadMore={() => void deals.fetchNextPage()}
          loadingMore={deals.isFetchingNextPage}
          onRetry={() => void deals.refetch()}
                  />

        {creating && (
          // The selected deal's account seeds the draft once; typing is never overwritten.
          <CreateDealPanel
            canWrite={canWrite}
            initialAccountId={selectedAccount}
            onClose={() => setCreating(false)}
            onCreated={(deal) => {
              setCreating(false);
              navigate(`/deals/${deal.id}`);
            }}
          />
        )}

        {selectedId !== null && (
          <DealDetailPanel
            key={selectedId}
            id={selectedId}
            canWrite={canWrite}
            canApprove={canApprove}
                        onClose={() => navigate('/deals')}
          />
        )}
      </div>
    </div>
  );
}
