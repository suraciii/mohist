import { EventEmitter } from 'node:events'
import { describe, expect, it, vi } from 'vitest'
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
    enforceUniqueRequestIds?: boolean
    emitCompletionOnTurnStart?: boolean
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
  const listeners = new Set<(message: unknown) => void>()
  const seenRequestIds = new Set<number>()

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
      if (options.enforceUniqueRequestIds) {
        if (seenRequestIds.has(request.id)) throw new Error(`duplicate response id ${request.id}`)
        seenRequestIds.add(request.id)
      }
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
      if (request.method === 'thread/start') {
        return {
          jsonrpc: '2.0',
          id: request.id,
          result: { thread: { id: 'thread-runtime', cwd: '/work' } },
        } as unknown as R
      }
      if (request.method === 'turn/start') {
        if (options.emitCompletionOnTurnStart) {
          setTimeout(() => {
            for (const listener of listeners) {
              listener({
                type: 'turn/completed',
                threadId: 'thread-runtime',
                turnId: 'turn-runtime',
                status: 'completed',
              })
            }
          }, 0)
        }
        return {
          jsonrpc: '2.0',
          id: request.id,
          result: { turn: { id: 'turn-runtime', status: 'in_progress' } },
        } as unknown as R
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
      listeners.add(listener)
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
      return () => listeners.delete(listener)
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

function lifecycleHandle(): CodexServerHandle & { readonly methods: string[] } {
  const methods: string[] = []
  const listeners = new Set<(message: unknown) => void>()
  let turnOrdinal = 0
  let threadOrdinal = 0
  const emitLater = (messages: readonly unknown[]) => {
    setTimeout(() => {
      for (const message of messages) for (const listener of listeners) listener(message)
    }, 0)
  }
  return {
    codexHome: MANAGED_CODEX_HOME,
    methods,
    async send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R> {
      methods.push(request.method)
      switch (request.method) {
        case 'initialize':
          return {
            jsonrpc: '2.0',
            id: request.id,
            result: { protocolVersion: 'v2', codexHome: MANAGED_CODEX_HOME, userAgent: 'codex/0.153.0' },
          } as unknown as R
        case 'model/list':
          return {
            jsonrpc: '2.0',
            id: request.id,
            result: { models: [{ id: 'gpt-5' }], complete: true },
          } as unknown as R
        case 'thread/start': {
          const threadId = `thread-${++threadOrdinal}`
          return { jsonrpc: '2.0', id: request.id, result: { threadId, cwd: '/work' } } as unknown as R
        }
        case 'thread/resume': {
          const threadId = (request.params as { readonly threadId?: string } | undefined)?.threadId ?? 'thread-1'
          return { jsonrpc: '2.0', id: request.id, result: { threadId, cwd: '/work' } } as unknown as R
        }
        case 'turn/start': {
          const turnId = `turn-${++turnOrdinal}`
          const threadId = (request.params as { readonly threadId?: string } | undefined)?.threadId ?? 'thread-1'
          emitLater([
            { type: 'agentMessage', text: 'done' },
            { type: 'turn/completed', threadId, turnId, status: 'completed' },
          ])
          return {
            jsonrpc: '2.0',
            id: request.id,
            result: { threadId: 'thread-1', turnId, status: 'inProgress' },
          } as unknown as R
        }
        case 'turn/steer':
          return { jsonrpc: '2.0', id: request.id, result: { accepted: true } } as unknown as R
        case 'turn/interrupt':
          return { jsonrpc: '2.0', id: request.id, result: { accepted: true } } as unknown as R
        case 'thread/compact/start': {
          const turnId = `compact-${++turnOrdinal}`
          const threadId = (request.params as { readonly threadId?: string } | undefined)?.threadId ?? 'thread-1'
          emitLater([
            { type: 'contextCompaction', threadId, turnId },
            { type: 'turn/completed', threadId, turnId, status: 'completed' },
          ])
          return { jsonrpc: '2.0', id: request.id, result: { threadId, turnId } } as unknown as R
        }
        default:
          throw new Error(`Unexpected method ${request.method}`)
      }
    },
    notify() {
      return true
    },
    denyServerRequest: () => undefined,
    subscribe(listener: (message: unknown) => void) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    async close() {
      listeners.clear()
    },
  } as unknown as CodexServerHandle & { readonly methods: string[] }
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

  it('shares request ids across initialize, catalog, thread, and turn calls', async () => {
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory: async () => fakeHandle({ enforceUniqueRequestIds: true, emitCompletionOnTurnStart: true }),
      readinessProbe: passingProbe(),
    })

    await expect(runtime.start()).resolves.toMatchObject({ ok: true })
    await expect(
      runtime.runTurn({
        target: { runtime: 'codex', runtimeSessionId: null, workDir: '/work' },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
        options: { model: 'gpt-5' },
      }),
    ).resolves.toMatchObject({ ok: true, value: { facts: { runtimeSessionId: 'thread-runtime' } } })
    expect(runtime.releaseWorkspace('/work')).toBe('ready')
    await runtime.shutdown({ clearDiagnostic: true })
  })

  it('retains Workspace ownership when turn/start has no confirmed outcome', async () => {
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory: async () => fakeHandle({ beforeInitialize: (method) => method === 'turn/start' }),
      readinessProbe: passingProbe(),
    })
    await expect(runtime.start()).resolves.toMatchObject({ ok: true })

    await expect(
      runtime.runTurn({
        target: { runtime: 'codex', runtimeSessionId: null, workDir: '/work' },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
        options: { model: 'gpt-5' },
      }),
    ).resolves.toMatchObject({ ok: false })
    expect(runtime.releaseWorkspace('/work')).toBe('busy')
    expect(runtime.releaseWorkspace('/other')).toBe('ready')
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

  it('retains the last complete snapshot while exposing a failed refresh through readiness', async () => {
    const liveCatalog = { models: [{ id: 'gpt-5' }], complete: true }
    const handle = fakeHandle({ catalog: liveCatalog })
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory: async () => handle,
      readinessProbe: passingProbe(),
    })

    await expect(runtime.start()).resolves.toMatchObject({ ok: true })
    const snapshot = runtime.catalog()
    expect(snapshot).not.toBeNull()
    liveCatalog.models.length = 0

    const refreshed = await runtime.refreshCatalog()

    expect(refreshed).toEqual({ changed: false, catalog: snapshot })
    expect(runtime.catalog()).toEqual(snapshot)
    expect(runtime.ready()).toBe(false)
    expect(runtime.diagnostic()).toMatchObject({ code: 'catalog-empty' })
    await runtime.shutdown({ clearDiagnostic: true })
  })

  it('bounds a server factory that never produces a child handle', async () => {
    vi.useFakeTimers()
    try {
      const runtime = new CodexRuntime({
        codexHome: MANAGED_CODEX_HOME,
        cwd: '/work',
        startupTimeoutMs: 25,
        serverFactory: async () => await new Promise<never>(() => {}),
        readinessProbe: passingProbe(),
      })
      const resultPromise = runtime.start()
      await vi.advanceTimersByTimeAsync(25)
      await expect(resultPromise).resolves.toMatchObject({
        ok: false,
        error: { kind: 'unavailable-runtime' },
      })
      expect(runtime.ready()).toBe(false)
    } finally {
      vi.useRealTimers()
    }
  })

  it('clears readiness after a protocol-failure notification from the child', async () => {
    let notifyFailure: ((message: unknown) => void) | null = null
    const handle = fakeHandle()
    ;(handle as { subscribe: (listener: (message: unknown) => void) => () => void }).subscribe = (listener) => {
      notifyFailure = listener
      return () => {
        notifyFailure = null
      }
    }
    const wiredRuntime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory: async () => handle,
      readinessProbe: passingProbe(),
    })
    await expect(wiredRuntime.start()).resolves.toMatchObject({ ok: true })
    expect(wiredRuntime.ready()).toBe(true)
    const failureListener = notifyFailure as ((message: unknown) => void) | null
    failureListener?.({
      jsonrpc: '2.0',
      method: 'protocol-failure',
      params: { reason: 'malformed-stdout', message: 'bad response' },
    })
    expect(wiredRuntime.ready()).toBe(false)
    expect(wiredRuntime.diagnostic()).toMatchObject({ code: 'protocol-failure' })
    await wiredRuntime.shutdown({ clearDiagnostic: true })
  })
})

describe('CodexRuntime app-server generations', () => {
  it('fences a lost generation and only serves newly admitted work from a fresh child', async () => {
    let failureListener: ((message: unknown) => void) | null = null
    const first = fakeHandle()
    ;(first as { subscribe: (listener: (message: unknown) => void) => () => void }).subscribe = (listener) => {
      failureListener = listener
      return () => {
        failureListener = null
      }
    }
    let spawned = 0
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory: async () => {
        spawned += 1
        return spawned === 1 ? first : fakeHandle()
      },
      readinessProbe: passingProbe(),
    })

    await expect(runtime.start()).resolves.toMatchObject({ ok: true, value: { generation: 1 } })

    // The child dies. The runtime stops claiming Codex work, drops the
    // volatile generation, and discards its per-generation Turn
    // correlation without replaying anything.
    const listener = failureListener as ((message: unknown) => void) | null
    listener?.({ jsonrpc: '2.0', method: 'protocol-failure', params: { reason: 'child-exit', message: 'gone' } })
    expect(runtime.ready()).toBe(false)
    expect(runtime.generation()).toBeNull()

    // A fresh app-server becomes a new generation and only then does the
    // runtime claim work again.
    await expect(runtime.start()).resolves.toMatchObject({ ok: true, value: { generation: 2 } })
    expect(spawned).toBe(2)
    await runtime.shutdown({ clearDiagnostic: true })
  })
})

describe('CodexRuntime AgentSession operations', () => {
  it('persists a new Thread before turn/start and drives follow-up, compact, and reset through the same app-server', async () => {
    const handle = lifecycleHandle()
    const runtime = new CodexRuntime({
      codexHome: MANAGED_CODEX_HOME,
      cwd: '/work',
      serverFactory: async () => handle,
      readinessProbe: passingProbe(),
    })
    await expect(runtime.start()).resolves.toMatchObject({ ok: true })

    const readySessions: string[] = []
    const events: string[] = []
    const observer = {
      onSessionReady: ({ runtimeSessionId }: { readonly runtimeSessionId: string }) => {
        readySessions.push(runtimeSessionId)
      },
      onEvent: (event: { readonly type: string }) => {
        events.push(event.type)
      },
    }
    const first = await runtime.runTurn(
      {
        target: { runtime: 'codex', runtimeSessionId: null, workDir: '/work' },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
        options: { model: 'gpt-5', reasoningEffort: null, variant: null },
      },
      new AbortController().signal,
      observer,
    )
    expect(first).toMatchObject({
      ok: true,
      value: { facts: { runtimeSessionId: 'thread-1', finalAssistantText: 'done' } },
    })
    expect(readySessions).toEqual(['thread-1'])
    expect(handle.methods.indexOf('thread/start')).toBeLessThan(handle.methods.indexOf('turn/start'))

    const followup = await runtime.followup(
      {
        target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: '/work' },
        prompt: 'continue',
        clientUserMessageId: 'input-2',
      },
      observer,
    )
    expect(followup).toMatchObject({
      ok: true,
      value: { facts: { runtimeSessionId: 'thread-1', finalAssistantText: 'done' } },
    })

    const compact = await runtime.compact(
      { target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: '/work' } },
      observer,
    )
    expect(compact).toMatchObject({ ok: true })
    expect(events).toContain('compaction')

    const reset = await runtime.reset({ target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: '/work' } })
    expect(reset).toMatchObject({ ok: true, value: { facts: { runtimeSessionId: 'thread-2' } } })
    expect(handle.methods.filter((method) => method === 'thread/start')).toHaveLength(2)
    await runtime.shutdown({ clearDiagnostic: true })
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
