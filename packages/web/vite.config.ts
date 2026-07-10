import path from 'path'
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// 需要 DOM 的 .test.ts（renderHook、document/matchMedia 依赖）。其余
// .test.ts 一律视为纯逻辑，跑 node 环境；新文件若确实要 DOM，加进这里。
const domDependentTestFiles = [
  'src/app/providers/LiveTaskProvider.inbox.test.ts',
  'src/app/providers/LiveTaskProvider.lifecycle.test.ts',
  'src/app/providers/LiveTaskProvider.transcript.test.ts',
  'src/pages/issue-detail/model/useIssueDetailMutations.test.ts',
  'src/shared/lib/theme/theme.test.ts',
  'src/widgets/coder-session/model/activity-cards.test.ts',
  'src/widgets/issue-event-timeline/useEventTimeline.test.ts',
  'src/widgets/issue-workflow/model/useWorkflowSessionFiltering.test.ts',
]

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
    // 根配置与 inline projects 必须一致，确保 setup 和测试共享同一组进程级单例。
    isolate: false,
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
    // jsdom 实例化是测试计算量的大头（CI 上约为测试体本身的 2 倍），而纯
    // 逻辑 .test.ts 根本不碰 DOM。按环境拆两个 project：.test.ts 走 node，
    // 组件/页面/跨切面测试走 jsdom。
    projects: [
      {
        extends: true,
        test: {
          name: 'node',
          isolate: false,
          environment: 'node',
          include: ['src/**/*.test.ts'],
          exclude: domDependentTestFiles,
        },
      },
      {
        extends: true,
        test: {
          name: 'jsdom',
          isolate: false,
          include: [
            'src/**/*.test.tsx',
            'tests/**/*.spec.tsx',
            ...domDependentTestFiles,
          ],
        },
      },
    ],
  },
})
