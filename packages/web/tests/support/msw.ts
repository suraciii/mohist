import { afterAll, afterEach, beforeAll } from 'vitest'
import { setupServer } from 'msw/node'

/**
 * 共享的 MSW server 实例。迁移期（isolate:true）各文件通过
 * `useMswServer(...)` 声明自己的 handler 并管理 listen/close 生命周期；
 * isolate:false 翻转前的整合步会把 listen/close 收敛到全局 setup 并将
 * onUnhandledRequest 收紧为 'error'
 * （openspec/changes/web-test-boundary-mocks plan §7）。
 *
 * 'error' 模式是 testing.md 硬约束 1（禁真实网络）的机器执行者：
 * 未被 mock 的请求直接失败，而不是静默放行。
 */
export const server = setupServer()

// node 环境（纯逻辑 project）没有 document base URL，fetch('/api/...') 的
// 相对路径在 Request 构造时就抛 TypeError，到不了 MSW 拦截层。listen 之后
// 在 MSW 代理外再包一层，把相对路径补成绝对 URL。MSW 的 close() 会把
// globalThis.fetch 整体还原，包装随之消失，无需自行拆除。
function absolutizeRelativeFetchUrls() {
  if (typeof window !== 'undefined') return
  const patchedFetch = globalThis.fetch.bind(globalThis)
  globalThis.fetch = ((input: RequestInfo | URL, init?: RequestInit) => {
    if (typeof input === 'string' && input.startsWith('/')) {
      return patchedFetch(new URL(input, 'http://localhost'), init)
    }
    return patchedFetch(input, init)
  }) as typeof fetch
}

export function useMswServer(...handlers: Parameters<typeof server.use>) {
  beforeAll(() => {
    server.listen({ onUnhandledRequest: 'error' })
    absolutizeRelativeFetchUrls()
    server.use(...handlers)
  })
  afterEach(() => {
    server.resetHandlers()
    server.use(...handlers)
  })
  afterAll(() => server.close())
}
