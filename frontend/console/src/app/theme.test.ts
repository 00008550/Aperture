import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  getTheme,
  readStoredTheme,
  resolveInitialTheme,
  setTheme,
  toggleTheme,
} from './theme';

// Emulate a `prefers-color-scheme` answer for the duration of a test.
function stubPrefersDark(dark: boolean) {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({
      matches: query.includes('dark') ? dark : false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia,
  );
}

beforeEach(() => {
  window.localStorage.clear();
  delete document.documentElement.dataset.theme;
});

afterEach(() => {
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

describe('theme resolution — default derives from prefers-color-scheme', () => {
  it('resolves dark when the OS asks for dark and no choice is stored', () => {
    stubPrefersDark(true);
    expect(readStoredTheme()).toBeNull();
    expect(resolveInitialTheme()).toBe('dark');
  });

  it('resolves light when the OS asks for light and no choice is stored', () => {
    stubPrefersDark(false);
    expect(resolveInitialTheme()).toBe('light');
  });

  it('a stored choice wins over the OS preference', () => {
    stubPrefersDark(true); // OS wants dark…
    setTheme('light'); // …but the viewer chose light
    expect(readStoredTheme()).toBe('light');
    expect(resolveInitialTheme()).toBe('light'); // the stored choice wins
  });
});

describe('setTheme / toggle — switches the token set and persists across a reload', () => {
  it('applies the theme to the document so the CSS token set switches', () => {
    setTheme('dark');
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(getTheme()).toBe('dark');

    setTheme('light');
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(getTheme()).toBe('light');
  });

  it('persists the choice to localStorage so it survives a reload (a fresh resolve reads it)', () => {
    stubPrefersDark(false);
    setTheme('dark');
    // A reload re-runs resolveInitialTheme() from a clean module state; the persisted value is
    // what it reads back — proving persistence across a remount/reload without the in-memory store.
    expect(window.localStorage.getItem('aperture.theme')).toBe('dark');
    expect(resolveInitialTheme()).toBe('dark');
  });

  it('toggles between the two themes', () => {
    setTheme('light');
    toggleTheme();
    expect(getTheme()).toBe('dark');
    toggleTheme();
    expect(getTheme()).toBe('light');
  });
});
