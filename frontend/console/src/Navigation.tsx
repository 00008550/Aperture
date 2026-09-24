import { Fragment } from 'react';
import { NavLink } from 'react-router';
import { Permissions, type Permission } from './permissions';

export interface NavItem {
  label: string;
  permission: Permission;
  /** The client route the item opens — see `app/router.tsx`, which gates it again. */
  path: string;
}

/**
 * The product's shape, in one list. Items are disabled rather than hidden so every role sees
 * what Aperture does and what they would need to be granted — hiding turns a permissions
 * question into a "the feature is gone" support ticket.
 */
export const NAV_ITEMS: readonly NavItem[] = [
  { label: 'Accounts', permission: Permissions.AccountsRead, path: '/accounts' },
  { label: 'Contacts', permission: Permissions.ContactsRead, path: '/contacts' },
  { label: 'Deals', permission: Permissions.DealsRead, path: '/deals' },
  { label: 'Orders', permission: Permissions.OrdersRead, path: '/orders' },
  { label: 'Timeline', permission: Permissions.TimelineRead, path: '/timeline' },
  { label: 'Administration', permission: Permissions.AdminUsers, path: '/administration' },
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
 * become the only thing standing between a caller and data — and a user who types the URL instead
 * meets the route guard in `app/router.tsx`, which consults the same `can()`.
 */
export function Navigation({ can, items = NAV_ITEMS }: NavigationProps) {
  return (
    <nav aria-label="Sections">
      <NavLink to="/" end>
        Overview
      </NavLink>
      {items.map((item) => {
        const allowed = can(item.permission);
        if (allowed) {
          // NavLink sets aria-current="page" on the active section, which styles.css keys on.
          return (
            <NavLink key={item.label} to={item.path} data-denied={false}>
              {item.label}
            </NavLink>
          );
        }
        const reasonId = `nav-denied-${item.path.slice(1)}`;
        return (
          <Fragment key={item.label}>
            <a
              // No href when denied: it cannot be followed by keyboard, middle-click or "open in new
              // tab", and it is skipped by Tab, like every denied control (see
              // `a11y/GuardedButton.tsx`). role="link" + aria-disabled is the ARIA disabled-link
              // pattern: assistive tech announces "Orders, link, unavailable" rather than bare text
              // (an href-less anchor is a generic element and carries no name at all). The reason is
              // its accessible description, never its name; `title` is not used, since a browser
              // may promote it to the name.
              role="link"
              aria-disabled="true"
              data-denied
              data-denied-reason={`Requires ${item.permission}`}
              aria-describedby={reasonId}
            >
              {item.label}
              <span className="mono lock" aria-hidden="true">
                {' '}
                locked
              </span>
            </a>
            {/* Outside the anchor, so the reason describes it without joining its name. */}
            <span id={reasonId} className="visually-hidden">
              Requires {item.permission}
            </span>
          </Fragment>
        );
      })}
    </nav>
  );
}
