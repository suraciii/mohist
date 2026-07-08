import path from 'path'
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
      '@microsoft/signalr': path.resolve(__dirname, './tests/support/signalr-fake.ts'),
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./tests/setup.ts'],
    globals: true,
    testTimeout: 30_000,
    hookTimeout: 30_000,
    maxWorkers: 1,
    include: ['tests/a11y/**/*.test.tsx'],
  },
})
