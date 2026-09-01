import { vi } from 'vitest'

/**
 * sonner 的测试替身，经 vite.config.ts 的 test.alias 全局生效——所有测试
 * 文件看到同一个模块实现，不产生 per-file 注册表分叉，与 isolate:false
 * 终局兼容。
 *
 * toast 是出站通知边界：产品契约是"以什么文案/等级发出了通知"，由
 * 记录断言表达；真渲染 Toaster 的动画/时序不属于被测行为。断言用
 * `import { toast } from 'sonner'` 后直接 `expect(toast.error)...`；
 * 调用记录由 setup.ts 的全局 afterEach 复位。
 */
export const toast = Object.assign(vi.fn(), {
  success: vi.fn(),
  error: vi.fn(),
  info: vi.fn(),
  warning: vi.fn(),
  message: vi.fn(),
  dismiss: vi.fn(),
  loading: vi.fn(),
  promise: vi.fn(),
})

export function Toaster() {
  return null
}

export function resetSonnerFake() {
  toast.mockClear()
  toast.success.mockClear()
  toast.error.mockClear()
  toast.info.mockClear()
  toast.warning.mockClear()
  toast.message.mockClear()
  toast.dismiss.mockClear()
  toast.loading.mockClear()
  toast.promise.mockClear()
}
