import type { ReactNode } from 'react';
import { BlockField } from '../field/BlockField';
import { Navigation } from '../Navigation';
import { clearAccessToken } from '../auth';
import { useTheme, toggleTheme, type Theme } from './theme';
import type { Permission } from '../permissions';

/**
 * The console shell: the reactive Aurora Glass field on its own `z-index` tier, with the frosted
 * sidebar and the routed/session content floating above it (see `styles.css` layer tiers). The
 * shell is theme-agnostic — every colour it shows comes from the theme tokens, so it is correct in
 * both light and dark, and the field behind it re-reads those tokens when the viewer switches.
 */

function ThemeToggle() {
  const theme = useTheme();
  const next: Theme = theme === 'dark' ? 'light' : 'dark';
  // The glyph shows the *current* theme; the label states what a click does, so screen-reader and
  // sighted users get the same, unambiguous affordance.
  return (
    <button
      type="button"
      className="theme-toggle"
      onClick={() => toggleTheme()}
      aria-label={`Switch to ${next} theme`}
      title={`Switch to ${next} theme`}
    >
      <span className="glyph" aria-hidden="true">
        {theme === 'dark' ? '◑' : '◐'}
      </span>
      <span>{theme === 'dark' ? 'Dark' : 'Light'} theme</span>
    </button>
  );
}

export interface ShellProps {
  /** Fail-closed permission check — see `useSession`. Gates the navigation exactly as before. */
  can: (permission: Permission) => boolean;
  /** The routed / session content that floats over the field. */
  children: ReactNode;
}

export function Shell({ can, children }: ShellProps) {
  return (
    <>
      <BlockField />
      <div className="shell">
        <aside className="side">
          <div className="brand">
            Aperture
            <small>order &amp; deal desk</small>
          </div>

          <Navigation can={can} />

          <ThemeToggle />

          <button type="button" className="link" onClick={() => clearAccessToken()}>
            Sign out
          </button>
        </aside>

        <main>{children}</main>
      </div>
    </>
  );
}
