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
    minify: true,
    sourcemap: false,
  },
  test: {
    environment: 'node',
    setupFiles: ['./tests/setup.ts'],
    globals: true,
    // 根配置与 inline projects 必须一致，确保 setup 和测试共享同一组进程级单例。
    isolate: false,
    maxWorkers: 4,
    testTimeout: 10_000,
    hookTimeout: 10_000,
    // mock/stub 的恢复交给机器而不是各文件的自觉——isolate:false 终局下
    // 跨文件卫生必须是机械保证（openspec/changes/web-test-boundary-mocks）。
    restoreMocks: true,
    unstubGlobals: true,
    unstubEnvs: true,
    // 真外部边界的全局替身：所有文件看到同一实现，无 per-file 模块注册表
    // 分叉（与 isolate:false 兼容）。这是唯一许可的"模块替换"形式，
    // 新增替身需在 openspec/changes/web-test-boundary-mocks 方案内登记。
    alias: {
      sonner: path.resolve(__dirname, './tests/support/sonner-fake.ts'),
      '@microsoft/signalr': path.resolve(__dirname, './tests/support/signalr-fake.ts'),
    },
    exclude: [
      '**/node_modules/**',
      '**/dist/**',
      '**/tests/browser/**',
      'node_modules/**',
      'dist/**',
      'tests/browser/**',
    ],
    // Test suffix owns the environment. Pure logic uses .test.ts; DOM tests
    // without JSX use .dom.test.ts; JSX and spec files use jsdom.
    projects: [
      {
        extends: true,
        test: {
          name: 'node',
          isolate: false,
          environment: 'node',
          include: ['src/**/*.test.ts'],
          exclude: ['src/**/*.dom.test.ts'],
        },
      },
      {
        extends: true,
        test: {
          name: 'jsdom',
          isolate: false,
          environment: 'jsdom',
          testTimeout: 30_000,
          include: [
            'src/**/*.test.tsx',
            'src/**/*.dom.test.ts',
            'src/**/*.spec.tsx',
            'tests/**/*.spec.tsx',
          ],
        },
      },
    ],
  },
})
