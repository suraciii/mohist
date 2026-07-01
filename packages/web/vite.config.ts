import path from 'path'
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 5173,
    host: '0.0.0.0',
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:3456',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://127.0.0.1:3456',
        changeOrigin: true,
        ws: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    minify: false,
    sourcemap: true,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./tests/setup.ts'],
    globals: true,
    testTimeout: 10_000,
    hookTimeout: 10_000,
    pool: 'forks',
    maxWorkers: 3,
    exclude: [
      '**/*.a11y.spec.ts',
      '**/node_modules/**',
      '**/dist/**',
      '**/tests/a11y/**',
      '**/tests/e2e/**',
      '**/e2e/**',
      'tests/e2e/**',
      'node_modules/**',
      'dist/**',
      'tests/a11y/**',
      'tests/e2e/**',
    ],
  },
})
