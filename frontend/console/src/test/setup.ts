import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';
import { clearAccessToken } from '../auth';

afterEach(() => {
  cleanup();
  // clearAccessToken, not sessionStorage.clear(): the token store also caches the value in
  // module scope, and clearing only the storage would leak a signed-in state between tests.
  clearAccessToken();
});
