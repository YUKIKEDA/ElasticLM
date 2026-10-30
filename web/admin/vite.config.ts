import { defineConfig } from 'vitest/config';

// design §15.2 (0.96, 0.100): Vite turns JSX into Preact itself, without the Preact preset. Every option the serving
// relies on is stated: the site root, the assets folder that elm-service serves, and no inlining (a data: URL is
// refused by the CSP default-src 'self').
export default defineConfig({
  base: '/',
  publicDir: false,
  oxc: { jsx: { runtime: 'automatic', importSource: 'preact' } },
  build: {
    outDir: 'dist',
    assetsDir: 'assets',
    assetsInlineLimit: 0,
    emptyOutDir: true,
  },
  test: { environment: 'happy-dom' },
});
