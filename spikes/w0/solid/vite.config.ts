import solid from 'vite-plugin-solid';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [solid({ hot: !process.env.VITEST })],
  resolve: process.env.VITEST ? { conditions: ['development', 'browser'] } : undefined,
  test: { environment: 'happy-dom' },
});
