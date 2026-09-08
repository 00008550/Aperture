import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';
import { clearAccessToken } from '../auth';

// jsdom has no ResizeObserver, which `BlockField` observes its canvas with. A no-op stub lets the
// field mount under test when a fake 2D context is supplied (the real behaviour is browser-verified).
if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
}

afterEach(() => {
  cleanup();
  // clearAccessToken, not sessionStorage.clear(): the token store also caches the value in
  // module scope, and clearing only the storage would leak a signed-in state between tests.
  clearAccessToken();
});
