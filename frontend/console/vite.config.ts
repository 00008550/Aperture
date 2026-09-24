import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // the console never talks to the API cross-origin; one origin keeps the bearer header
      // and any future cookie on the same site
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
    // Only the app stylesheet is processed (the rest stay stubbed), so `app/styles.test.ts` can read
    // it as text — jsdom does not lay out, and a selector collision is otherwise untestable.
    css: { include: [/styles\.css/] },
  },
});
