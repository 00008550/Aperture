import { useSyncExternalStore } from 'react';

/**
 * The console's one presentation fact: which theme the viewer chose. Aurora Glass (010-P2) makes
 * light and dark both first-class, so this is a real, persisted choice — not a dark-only default.
 *
 * Three viewer states resolve, in priority order:
 *   1. an explicit stored choice (localStorage) — the viewer picked one and it survives reloads;
 *   2. otherwise the OS preference (`prefers-color-scheme`);
 *   3. light as the final fallback (the bare `:root` token set).
 *
 * localStorage, not sessionStorage: a theme preference is a durable comfort choice, unlike the
 * bearer token (which dies with the tab, see `auth.ts`). It carries no authority and no session
 * data, so persisting it across tabs and reloads is correct.
 */
export type Theme = 'light' | 'dark';

const STORAGE_KEY = 'aperture.theme';

type Listener = () => void;
const listeners = new Set<Listener>();

/** Whether the OS asks for a dark UI. Guarded so it is safe under jsdom / SSR. */
export function systemPrefersDark(): boolean {
  return typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches === true;
}

/** The explicitly stored choice, or null when the viewer has never chosen. */
export function readStoredTheme(): Theme | null {
  try {
    const v = window.localStorage.getItem(STORAGE_KEY);
    return v === 'light' || v === 'dark' ? v : null;
  } catch {
    // Storage can throw (private mode, blocked cookies). No stored choice → fall through to the OS.
    return null;
  }
}

/** Resolve the theme a fresh load should use: stored choice first, else the OS preference, else light. */
export function resolveInitialTheme(): Theme {
  return readStoredTheme() ?? (systemPrefersDark() ? 'dark' : 'light');
}

// useSyncExternalStore compares snapshots by identity; a 'light' | 'dark' string is a primitive, so
// the cached snapshot changes identity only when the value actually changes.
let snapshot: Theme = resolveInitialTheme();

/** Reflect the active theme onto the document so the CSS token sets switch (`[data-theme=…]`). */
function applyToDocument(theme: Theme): void {
  if (typeof document !== 'undefined') {
    document.documentElement.dataset.theme = theme;
  }
}

// Apply once on module load so the first paint already carries the resolved theme — no flash.
applyToDocument(snapshot);

function emit(): void {
  for (const listener of listeners) listener();
}

export function getTheme(): Theme {
  return snapshot;
}

export function setTheme(theme: Theme): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, theme);
  } catch {
    // Keep the in-memory choice even when persistence fails: the theme still switches, it just
    // does not survive a reload.
  }
  snapshot = theme;
  applyToDocument(theme);
  emit();
}

export function toggleTheme(): void {
  setTheme(snapshot === 'dark' ? 'light' : 'dark');
}

function subscribe(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** The active theme, re-rendering the tree when the viewer switches it. */
export function useTheme(): Theme {
  return useSyncExternalStore(subscribe, getTheme, getTheme);
}
