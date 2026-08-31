import { Permissions, type Permission } from './permissions';

export interface NavItem {
  label: string;
  permission: Permission;
}

/**
 * The product's shape, in one list. Items are disabled rather than hidden so every role sees
 * what Aperture does and what they would need to be granted — hiding turns a permissions
 * question into a "the feature is gone" support ticket.
 */
export const NAV_ITEMS: readonly NavItem[] = [
  { label: 'Accounts', permission: Permissions.AccountsRead },
  { label: 'Contacts', permission: Permissions.ContactsRead },
  { label: 'Deals', permission: Permissions.DealsRead },
  { label: 'Orders', permission: Permissions.OrdersRead },
  { label: 'Timeline', permission: Permissions.TimelineRead },
  { label: 'Administration', permission: Permissions.AdminUsers },
];

export interface NavigationProps {
  /** Fail-closed permission check — see `useSession`. */
  can: (permission: Permission) => boolean;
  items?: readonly NavItem[];
}

/**
 * The permission gate. It is **convenience, never enforcement**: a user who edits the DOM to
 * re-enable an item gets the same 403 from the API that they would have got anyway
 * (`Aperture.Api.Tests/ConsoleGatedRouteTests.cs` asserts exactly that). Nothing here is allowed to
 * become the only thing standing between a caller and data.
 */
export function Navigation({ can, items = NAV_ITEMS }: NavigationProps) {
  return (
    <nav aria-label="Sections">
      <a href="#overview" aria-current="page">
        Overview
      </a>
      {items.map((item) => {
        const allowed = can(item.permission);
        return (
          <a
            key={item.label}
            // No href when denied: an anchor without one is not a link, so it cannot be
            // followed by keyboard, middle-click or "open in new tab" either.
            {...(allowed ? { href: `#${item.label.toLowerCase()}` } : {})}
            aria-disabled={allowed ? undefined : true}
            data-denied={!allowed}
            title={allowed ? undefined : `Requires ${item.permission}`}
          >
            {item.label}
            {!allowed && (
              <span className="mono lock" aria-label={`Requires ${item.permission}`}>
                {' '}
                locked
              </span>
            )}
          </a>
        );
      })}
    </nav>
  );
}
