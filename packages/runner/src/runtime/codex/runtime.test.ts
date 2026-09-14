import { EventEmitter } from 'node:events'
import { describe, expect, it } from 'vitest'
import { CodexRuntime } from './runtime.js'
import type { CodexServerHandle } from './server-process.js'
import type { CodexAuthenticationProbe, CodexCatalogLoader, CodexCliProbe, CodexReadinessProbe } from './readiness.js'
import type { CodexCatalog } from './types.js'

const MANAGED_CODEX_HOME = '/runner/.mohist/codex'

/**
 * Build a fake server handle that emits the canonical initialize
 * response and a one-model model/list response. Tests can override
 * `codexHome`, the response, or reject the request.
 */
function fakeHandle(
  options: {
    codexHome?: string
    initializeCodexHome?: string
    initializeProtocolVersion?: string
    catalog?: { models: Array<{ id: string; displayName?: string }>; complete: boolean }
    beforeInitialize?: (method: string) => boolean
    onInitialize?: () => void
    onNotify?: (method: string) => void
  } = {},
): CodexServerHandle {
  const catalog = options.catalog ?? { models: [{ id: 'gpt-5' }], complete: true }
  const writes: string[] = []
  const stdin = {
    write(chunk: string | Buffer, cb?: (err?: Error | null) => void) {
      const text = typeof chunk === 'string' ? chunk : chunk.toString('utf8')
      for (const line of text.split('\n')) {
        if (line.length === 0) continue
        writes.push(line)
        try {
          const envelope = JSON.parse(line) as { method?: string }
          if (envelope.method === 'initialized') options.onNotify?.(envelope.method)
        } catch {
          /* ignore malformed */
        }
      }
      cb?.(null)
      return true
    },
    end() {
      /* no-op */
    },
  }
  const stdout = new EventEmitter()
  ;(stdout as unknown as { setEncoding: (encoding: BufferEncoding) => void }).setEncoding = () => undefined
  const stderr = new EventEmitter()
  ;(stderr as unknown as { setEncoding: (encoding: BufferEncoding) => void }).setEncoding = () => undefined

  function buildInitializeResponse(id: number) {
    return {
      jsonrpc: '2.0',
      id,
      result: {
        protocolVersion: options.initializeProtocolVersion ?? 'v2',
        codexHome: options.initializeCodexHome ?? MANAGED_CODEX_HOME,
        userAgent: 'codex/0.153.0',
      },
    }
  }
  function buildModelListResponse(id: number) {
    return { jsonrpc: '2.0', id, result: catalog }
  }

  return {
    codexHome: options.codexHome ?? MANAGED_CODEX_HOME,
    async send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R> {
      if (options.beforeInitialize?.(request.method)) {
        throw new Error('child exited before response')
      }
      if (request.method === 'initialize') {
        options.onInitialize?.()
        return buildInitializeResponse(request.id) as unknown as R
      }
      if (request.method === 'model/list') {
        return buildModelListResponse(request.id) as unknown as R
      }
      throw new Error(`Unexpected method ${request.method}`)
    },
    notify<P>(notification: { readonly method: string; readonly params?: P }): boolean {
      writes.push(JSON.stringify(notification))
      options.onNotify?.(notification.method)
      return true
    },
    denyServerRequest: () => undefined,
    subscribe(listener: (message: unknown) => void) {
      stdout.on('data', (chunk: Buffer | string) => {
        const text = typeof chunk === 'string' ? chunk : chunk.toString('utf8')
        for (const line of text.split('\n')) {
          if (line.length === 0) continue
          try {
            listener(JSON.parse(line))
          } catch {
            listener({ method: 'protocol-failure', params: { reason: 'malformed-stdout' } })
          }
        }
      })
      return () => undefined
    },
    async close() {
      /* no-op */
    },
  } as unknown as CodexServerHandle
}

function passingProbe(
  overrides: Partial<{
    binary: string | null
    version: string | null
    authenticated: boolean
    catalog: CodexCatalog | null
  }> = {},
): CodexReadinessProbe {
  const cli: CodexCliProbe = {
    async resolveCodexBinary() {
      return overrides.binary === undefined ? '/usr/local/bin/codex' : overrides.binary
    },
    async resolveCodexVersion() {
      return overrides.version === undefined ? '0.153.0' : overrides.version
    },
  }
  const authentication: CodexAuthenticationProbe = {
    async hasManagedAuthentication() {
      return overrides.authenticated ?? true
    },
  }
  const catalog: CodexCatalogLoader = {
    async loadCatalog() {
      if ('catalog' in overrides) return overrides.catalog ?? null
      return {
        models: [
          {
            id: 'gpt-5',
            displayName: null,
            reasoningEfforts: [],
            defaultReasoningEffort: null,
            supportsReasoningEffort: true,
          },
        ],
        complete: true,
        capabilityRevision: 'rev-1',
      }
    },
  }
  return { cli, authentication, catalog }
}

describe('CodexRuntime spawn + handshake happy path', () => {
  it('runs the initialize handshake and reports ready when the probe succeeds', async () => {
    let initializeCount = 0
    let notifyCount = 0
    const serverFactory = async () =>
      fakeHandle({
        onInitialize: () => (initializeCount += 1),
        onNotify: (method) => {
          if (method === 'initialized') notifyCount += 1
        },
      })
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory,
      readinessProbe: passingProbe(),
    })
    const result = await runtime.start()
    expect(result).toMatchObject({ ok: true })
    expect(initializeCount).toBe(1)
    expect(notifyCount).toBe(1)
    if (!result.ok) throw new Error('expected ready')
    expect(result.value.generation).toBe(1)
    expect(result.value.catalog?.models).toHaveLength(1)
    await runtime.shutdown({ clearDiagnostic: true })
  })
})

describe('CodexRuntime initialization refusal', () => {
  it('refuses a non-managed codexHome as incompatible-runtime', async () => {
    const serverFactory = async () => fakeHandle({ initializeCodexHome: '/home/person/.codex' })
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory,
      readinessProbe: passingProbe(),
    })
    const result = await runtime.start()
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'incompatible-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics.some((diagnostic) => diagnostic.code === 'incompatible-runtime')).toBe(true)
    expect(runtime.ready()).toBe(false)
  })

  it('marks the runtime not ready when the child exits before initialize completes', async () => {
    const serverFactory = async () => fakeHandle({ beforeInitialize: () => true })
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory,
      readinessProbe: passingProbe(),
    })
    const result = await runtime.start()
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics.some((diagnostic) => diagnostic.code === 'initialize-failed')).toBe(true)
    expect(runtime.ready()).toBe(false)
  })
})

describe('CodexRuntime readiness gate', () => {
  it('marks the runtime not ready when the CLI binary is missing', async () => {
    const serverFactory = async () => fakeHandle()
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory,
      readinessProbe: passingProbe({ binary: null }),
    })
    const result = await runtime.start()
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics.some((diagnostic) => diagnostic.code === 'cli-not-executable')).toBe(true)
    expect(runtime.ready()).toBe(false)
  })

  it('marks the runtime not ready when the CLI version is outside the supported range', async () => {
    const serverFactory = async () => fakeHandle()
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory,
      readinessProbe: passingProbe({ version: '0.152.0' }),
    })
    const result = await runtime.start()
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'incompatible-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics.some((diagnostic) => diagnostic.code === 'cli-version-incompatible')).toBe(true)
  })

  it('marks the runtime not ready when managed authentication is missing', async () => {
    const serverFactory = async () => fakeHandle()
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory,
      readinessProbe: passingProbe({ authenticated: false }),
    })
    const result = await runtime.start()
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics.some((diagnostic) => diagnostic.code === 'managed-auth-missing')).toBe(true)
  })

  it('marks the runtime not ready when the catalog is empty', async () => {
    const serverFactory = async () => fakeHandle({ catalog: { models: [], complete: true } })
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory,
      readinessProbe: passingProbe(),
    })
    const result = await runtime.start()
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics.some((diagnostic) => diagnostic.code === 'catalog-empty')).toBe(true)
  })
})

describe('CodexRuntime shutdown', () => {
  it('closes the spawned handle within the bounded deadline', async () => {
    let closeCount = 0
    const handle = fakeHandle()
    const original = handle.close.bind(handle)
    ;(handle as unknown as { close: () => Promise<void> }).close = async () => {
      closeCount += 1
      await original()
    }
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory: async () => handle,
      readinessProbe: passingProbe(),
      runtimeShutdownTimeoutMs: 100,
    })
    const result = await runtime.start()
    expect(result).toMatchObject({ ok: true })
    await runtime.shutdown()
    expect(closeCount).toBe(1)
  })

  it('reports unavailable-runtime when no readiness probe is provided', async () => {
    const serverFactory = async () => fakeHandle()
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory,
    })
    const result = await runtime.start()
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics.some((diagnostic) => diagnostic.code === 'readiness-probe-missing')).toBe(true)
  })
})
