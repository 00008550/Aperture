import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // the console never talks to the API cross-origin; one origin keeps cookies simple
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
    },
  },
});
