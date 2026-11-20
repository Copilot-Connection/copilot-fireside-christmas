import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../server/wwwroot', emptyOutDir: true },
  server: {
    proxy: {
      '/api': 'http://127.0.0.1:5180',
      '/auth': 'http://127.0.0.1:5180',
      '/live': { target: 'http://127.0.0.1:5180', ws: true },
    },
  },
  test: { environment: 'jsdom', setupFiles: './src/test-setup.ts', exclude: ['e2e/**', 'node_modules/**'] },
});
