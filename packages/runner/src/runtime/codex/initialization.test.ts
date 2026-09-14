import { describe, expect, it, vi } from 'vitest'
import { performCodexInitialization, type CodexInitializationTransport } from './initialization.js'

function buildTransport(
  overrides: Partial<{
    send: (request: {
      readonly method: 'initialize'
      readonly params?: unknown
      readonly id: number
    }) => Promise<unknown>
    notify: (notification: { readonly method: 'initialized'; readonly params?: unknown }) => boolean
    hasExited: () => boolean
  }> = {},
): CodexInitializationTransport & {
  readonly sentCalls: Array<{ readonly method: 'initialize'; readonly id: number }>
  readonly notifications: Array<{ readonly method: 'initialized'; readonly params?: unknown }>
} {
  const sentCalls: Array<{ readonly method: 'initialize'; readonly id: number }> = []
  const notifications: Array<{ readonly method: 'initialized'; readonly params?: unknown }> = []
  const transport: CodexInitializationTransport & {
    sentCalls: typeof sentCalls
    notifications: typeof notifications
  } = {
    sentCalls,
    notifications,
    async send<R>(request: {
      readonly method: 'initialize'
      readonly params?: unknown
      readonly id: number
    }): Promise<R> {
      sentCalls.push({ method: request.method, id: request.id })
      if (overrides.send) {
        return (await overrides.send(request)) as R
      }
      return {
        jsonrpc: '2.0',
        id: request.id,
        result: {
          protocolVersion: 'v2',
          codexHome: '/runner/.mohist/codex',
          userAgent: 'codex/0.153.0',
        },
      } as unknown as R
    },
    notify(notification) {
      notifications.push({ method: notification.method, params: notification.params })
      if (overrides.notify) return overrides.notify(notification)
      return true
    },
    hasExited() {
      if (overrides.hasExited) return overrides.hasExited()
      return false
    },
  }
  return transport
}

describe('performCodexInitialization', () => {
  it('emits an initialize request with no experimental client capabilities', async () => {
    const transport = buildTransport()
    const result = await performCodexInitialization(transport, {
      managedCodexHome: '/runner/.mohist/codex',
      startupTimeoutMs: 5_000,
    })
    expect(result).toMatchObject({ ok: true })
    expect(transport.sentCalls).toHaveLength(1)
    expect(transport.sentCalls[0]).toEqual({ method: 'initialize', id: 1 })
    expect(transport.notifications).toEqual([{ method: 'initialized', params: undefined }])
  })

  it('rejects a non-managed codexHome response as incompatible-runtime', async () => {
    const transport = buildTransport({
      send: async (request) => ({
        jsonrpc: '2.0',
        id: request.id,
        result: {
          protocolVersion: 'v2',
          codexHome: '/home/person/.codex',
          userAgent: 'codex/0.153.0',
        },
      }),
    })
    const result = await performCodexInitialization(transport, {
      managedCodexHome: '/runner/.mohist/codex',
      startupTimeoutMs: 5_000,
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'incompatible-runtime' },
    })
    // The notification MUST NOT be sent when the response did not
    // match the managed path.
    expect(transport.notifications).toHaveLength(0)
  })

  it('rejects an initialize response that does not match the locked subset', async () => {
    const transport = buildTransport({
      send: async (request) => ({
        jsonrpc: '2.0',
        id: request.id,
        result: { protocolVersion: 'v2' }, // missing codexHome
      }),
    })
    const result = await performCodexInitialization(transport, {
      managedCodexHome: '/runner/.mohist/codex',
      startupTimeoutMs: 5_000,
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'incompatible-runtime' },
    })
  })

  it('reports unavailable-runtime when the transport rejects the initialize request', async () => {
    const transport = buildTransport({
      send: async () => {
        throw new Error('connection reset')
      },
    })
    const result = await performCodexInitialization(transport, {
      managedCodexHome: '/runner/.mohist/codex',
      startupTimeoutMs: 5_000,
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (!result.ok) {
      const code = result.error.diagnostics[0]?.code
      expect(code).toBe('initialize-failed')
    }
  })

  it('reports unavailable-runtime when the child exits before initialize completes', async () => {
    const transport = buildTransport({ hasExited: () => true })
    const result = await performCodexInitialization(transport, {
      managedCodexHome: '/runner/.mohist/codex',
      startupTimeoutMs: 5_000,
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    expect(transport.sentCalls).toHaveLength(0)
  })

  it('preserves the user-agent when the server advertises it', async () => {
    const transport = buildTransport()
    const result = await performCodexInitialization(transport, {
      managedCodexHome: '/runner/.mohist/codex',
      startupTimeoutMs: 5_000,
    })
    if (!result.ok) throw new Error('expected ok')
    expect(result.value.userAgent).toBe('codex/0.153.0')
    expect(result.value.protocolVersion).toBe('v2')
    expect(result.value.codexHome).toBe('/runner/.mohist/codex')
  })

  it('records a warning when the initialized notification write fails', async () => {
    const transport = buildTransport({ notify: () => false })
    const result = await performCodexInitialization(transport, {
      managedCodexHome: '/runner/.mohist/codex',
      startupTimeoutMs: 5_000,
    })
    expect(result).toMatchObject({ ok: true })
    if (!result.ok) throw new Error('expected ok')
    const codes = result.diagnostics.map((diagnostic) => diagnostic.code)
    expect(codes).toContain('initialized-notify-failed')
  })

  it('does not introduce experimental capabilities on the initialize envelope', async () => {
    const captured: unknown[] = []
    const transport: CodexInitializationTransport = {
      async send<R>(request: {
        readonly method: 'initialize'
        readonly params?: unknown
        readonly id: number
      }): Promise<R> {
        captured.push(request.params)
        return {
          jsonrpc: '2.0',
          id: request.id,
          result: {
            protocolVersion: 'v2',
            codexHome: '/runner/.mohist/codex',
          },
        } as unknown as R
      },
      notify: vi.fn().mockReturnValue(true),
      hasExited: () => false,
    }
    await performCodexInitialization(transport, {
      managedCodexHome: '/runner/.mohist/codex',
      startupTimeoutMs: 5_000,
    })
    expect(captured).toHaveLength(1)
    const envelope = captured[0] as Record<string, unknown>
    const experimentKeys = Object.keys(envelope).filter(
      (key) => key.includes('experimental') || key.includes('capabilities'),
    )
    expect(experimentKeys).toEqual([])
  })
})
