import { useSyncExternalStore } from 'react';

/**
 * The console holds one thing: the bearer token the API issued (001-P3). Everything else
 * about the session — tenant, permissions, scopes — is asked for, never stored, because a
 * cached copy of "what I may do" is a copy that can go stale in the permissive direction.
 *
 * sessionStorage, not localStorage: the token dies with the tab. A console left open on a
 * shared machine is the failure this closes; it costs a re-sign-in per tab.
 */
const STORAGE_KEY = 'aperture.access-token';

type Listener = () => void;
const listeners = new Set<Listener>();

function read(): string | null {
  try {
    return window.sessionStorage.getItem(STORAGE_KEY);
  } catch {
    // Storage can throw (private mode, blocked cookies). Failing closed here means the
    // console asks the user to sign in again rather than crashing on load.
    return null;
  }
}

// useSyncExternalStore compares snapshots by identity, so the string must be cached: reading
// storage on every render would return a fresh equal-but-not-identical value only when it
// changed, but caching also keeps the read off the render path.
let snapshot: string | null = read();

/**
 * Why the last session ended, when it ended because the API refused the token. It lives here
 * rather than in the query cache because the cache entry is keyed on the token, and the token
 * is precisely what has just been thrown away — the error would vanish with it, and the user
 * would be bounced back to a sign-in form that says nothing about why.
 */
let signOutReason: string | null = null;

function emit(): void {
  for (const listener of listeners) listener();
}

export function getAccessToken(): string | null {
  return snapshot;
}

export function getSignOutReason(): string | null {
  return signOutReason;
}

export function setAccessToken(token: string): void {
  try {
    window.sessionStorage.setItem(STORAGE_KEY, token);
  } catch {
    // Keep the in-memory token even when persistence fails: the session still works, it
    // just does not survive a reload.
  }
  snapshot = token;
  signOutReason = null;
  emit();
}

export function clearAccessToken(reason?: string): void {
  try {
    window.sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    /* nothing to do — the in-memory clear below is the one that matters */
  }
  snapshot = null;
  signOutReason = reason ?? null;
  emit();
}

function subscribe(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** The current token, re-rendering the tree when it is set or cleared. */
export function useAccessToken(): string | null {
  return useSyncExternalStore(subscribe, getAccessToken, getAccessToken);
}

/** Why the last token was discarded, or null after a deliberate sign-out or a fresh load. */
export function useSignOutReason(): string | null {
  return useSyncExternalStore(subscribe, getSignOutReason, getSignOutReason);
}
