import { describe, expect, it, vi } from 'vitest'
import {
  errorKindFor,
  isNonRecoverableProviderMessage,
  isNonRecoverableProviderRetry,
  normalizeInterrupted,
  normalizeInvalidInput,
  normalizeMissingSession,
  normalizePermissionRequired,
  normalizeTurnFailed,
  normalizeUnavailableRuntime,
  OpenCodeRuntime,
  parseModelIdentifier,
  getOpenCodeRuntimeFactory,
  createDefaultOpenCodeRuntime,
} from '../src/runtime/opencode/index.js'
import type { OpenCodeRuntimeFactory } from '../src/runtime/opencode/factory.js'
import type { RunnerFileSystem } from '../src/system/filesystem.js'
import type { OpenCodeRuntimeDeps } from '../src/runtime/opencode/runtime.js'
import type { OpencodeServerHandle } from '../src/runtime/opencode/server-process.js'
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from '../src/runtime/opencode/event-subscription.js'
import type { OpencodeClient } from '@opencode-ai/sdk/v2'
import * as runtimeModule from '../src/runtime/opencode/index.js'
import { MemoryFileSystem } from './support/memory-filesystem.js'
import { withTestRunnerResources } from './support/test-resources.js'

function deferred<T = void>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

class FakeSubscription implements RuntimeEventSubscription {
  private listeners = new Set<(event: RuntimeGlobalEvent) => void>()
  closed = false
  subscribeCalls = 0
  subscribe(listener: (event: RuntimeGlobalEvent) => void): () => void {
    this.subscribeCalls += 1
    if (this.closed) return () => {}
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }
  emit(event: RuntimeGlobalEvent): void {
    for (const listener of [...this.listeners]) {
      listener(event)
    }
  }
  async close(): Promise<void> {
    this.closed = true
    this.listeners.clear()
  }
}

interface FakeClientHandles {
  health: ReturnType<typeof vi.fn>
  sessionCreate: ReturnType<typeof vi.fn>
  sessionGet: ReturnType<typeof vi.fn>
  sessionStatus: ReturnType<typeof vi.fn>
  sessionMessages: ReturnType<typeof vi.fn>
}

interface BuildArgs {
  failStart?: boolean
  failHealth?: boolean
  failSessionCreate?: boolean
  rebuildDelayMs?: number
  resolveSession?: { runtimeSessionId: string; activeTurn: boolean }
  failSessionGet?: boolean
  failSessionStatus?: boolean
  closeGate?: Promise<void>
}

interface BuildResult {
  deps: OpenCodeRuntimeDeps
  subscription: FakeSubscription
  client: FakeClientHandles
  closed: { value: boolean }
  closeStarted: Promise<void>
}

function buildDeps(args: BuildArgs = {}): BuildResult {
  const subscription = new FakeSubscription()
  const closed = { value: false }
  let resolveCloseStarted!: () => void
  const closeStarted = new Promise<void>((resolve) => {
    resolveCloseStarted = resolve
  })
  const health = vi.fn(async () => ({ data: { ok: true } }))
  const sessionCreate = vi.fn(async (params: { directory?: string; model?: unknown }) => ({
    data: { id: `ses_${(params.directory ?? 'default').replace(/[^a-z0-9]+/gi, '_')}` },
  }))
  const resolved = args.resolveSession
  const sessionGet = vi.fn(async (params: { sessionID: string }) => {
    if (args.failSessionGet) {
      throw Object.assign(new Error('session get boom'), { status: 500 })
    }
    return { data: { id: params.sessionID } }
  })
  const sessionMessages = vi.fn(async () => ({ data: [], cursor: {} }))
  const sessionStatus = vi.fn(async () => {
    if (args.failSessionStatus) {
      throw Object.assign(new Error('status endpoint missing'), { status: 404 })
    }
    const type = resolved?.activeTurn ? 'streaming' : 'idle'
    return { data: { [resolved!.runtimeSessionId]: { type } } }
  })
  if (args.failHealth) {
    health.mockRejectedValueOnce(new Error('health boom'))
  }
  if (args.failSessionCreate) {
    sessionCreate.mockRejectedValueOnce(new Error('session create boom'))
  }
  const clientProxy = {
    global: { health },
    session: { create: sessionCreate, get: sessionGet, status: sessionStatus, messages: sessionMessages },
  }
  const clientHandles: FakeClientHandles = {
    health,
    sessionCreate,
    sessionGet,
    sessionStatus,
    sessionMessages,
  }
  const server: OpencodeServerHandle = {
    url: 'http://fake',
    directory: '/tmp/work',
    client: clientProxy as unknown as OpencodeClient,
    async close() {
      resolveCloseStarted()
      if (args.closeGate) await args.closeGate
      closed.value = true
    },
  }
  const deps: OpenCodeRuntimeDeps = {
    directory: '/tmp/work',
    serverFactory: async () => {
      if (args.failStart) throw new Error('spawn failed')
      return server
    },
    eventSubscriptionFactory: () => subscription,
    ...(args.rebuildDelayMs !== undefined ? { rebuildDelayMs: args.rebuildDelayMs } : {}),
  }
  return { deps, subscription, client: clientHandles, closed, closeStarted }
}

describe('parseModelIdentifier', () => {
  it('parses a simple provider/model', () => {
    expect(parseModelIdentifier('openai/gpt-5')).toEqual({
      kind: 'ok',
      value: { providerID: 'openai', modelID: 'gpt-5' },
    })
  })

  it('preserves the full remainder for multi-slash model IDs', () => {
    expect(parseModelIdentifier('openrouter/vendor/family/model')).toEqual({
      kind: 'ok',
      value: { providerID: 'openrouter', modelID: 'vendor/family/model' },
    })
  })

  it('rejects an empty model', () => {
    expect(parseModelIdentifier('').kind).toBe('failure')
  })

  it('rejects an identifier without a slash', () => {
    expect(parseModelIdentifier('gpt-5').kind).toBe('failure')
  })

  it('rejects an identifier with an empty provider', () => {
    expect(parseModelIdentifier('/gpt-5').kind).toBe('failure')
  })

  it('rejects an identifier with an empty model id', () => {
    expect(parseModelIdentifier('openai/').kind).toBe('failure')
  })
})

describe('error normalization', () => {
  it('errorKindFor maps 404 to missing-session', () => {
    expect(errorKindFor({ message: 'not found', status: 404 })).toBe('missing-session')
  })

  it('errorKindFor maps permission messages to permission-required', () => {
    expect(errorKindFor({ message: 'permission denied', status: 403 })).toBe('permission-required')
  })

  it('errorKindFor falls back to turn-failed for unknown errors', () => {
    expect(errorKindFor({ message: 'boom' })).toBe('turn-failed')
  })

  it('isNonRecoverableProviderMessage matches quota wording', () => {
    expect(isNonRecoverableProviderMessage('OpenAI quota exceeded')).toBe(true)
  })

  it('isNonRecoverableProviderMessage matches credit wording', () => {
    expect(isNonRecoverableProviderMessage('No credits remaining on your account')).toBe(true)
  })

  it('isNonRecoverableProviderMessage matches billing wording', () => {
    expect(isNonRecoverableProviderMessage('Billing issue: please update your card')).toBe(true)
  })

  it('isNonRecoverableProviderMessage matches Chinese wording', () => {
    expect(isNonRecoverableProviderMessage('账户额度已用完')).toBe(true)
  })

  it('isNonRecoverableProviderMessage matches usage-limit wording without matching rate limits', () => {
    expect(isNonRecoverableProviderMessage('Token Plan usage limit reached')).toBe(true)
    expect(isNonRecoverableProviderMessage('您已达到每周/每月使用上限，您的限额将在明天重置')).toBe(true)
    expect(isNonRecoverableProviderMessage('Rate limit exceeded, retry shortly')).toBe(false)
  })

  it('isNonRecoverableProviderRetry prefers structured quota reasons', () => {
    expect(
      isNonRecoverableProviderRetry({
        message: 'retry later',
        action: { reason: 'free_tier_limit' },
      }),
    ).toBe(true)
  })

  it('isNonRecoverableProviderMessage does not match a transient 429', () => {
    expect(isNonRecoverableProviderMessage('Rate limit exceeded, retry shortly')).toBe(false)
  })

  it('normalizeMissingSession includes a Reset hint', () => {
    const error = normalizeMissingSession()
    expect(error.kind).toBe('missing-session')
    expect(error.diagnostics.some((d) => d.message.toLowerCase().includes('reset'))).toBe(true)
  })

  it('normalizePermissionRequired gives a retryable failure when the headless reply cannot complete', () => {
    const error = normalizePermissionRequired()
    expect(error.kind).toBe('permission-required')
    expect(error.message).toMatch(/headless runtime/i)
    expect(error.diagnostics.some((d) => /restore.*retry/i.test(d.message))).toBe(true)
  })

  it('normalizeUnavailableRuntime carries a recovery diagnostic', () => {
    const error = normalizeUnavailableRuntime()
    expect(error.kind).toBe('unavailable-runtime')
    expect(error.diagnostics.length).toBeGreaterThan(0)
  })

  it('normalizeTurnFailed surfaces the provider message as the error message', () => {
    const error = normalizeTurnFailed({ message: 'OpenAI quota exceeded' })
    expect(error.kind).toBe('turn-failed')
    expect(error.message).toBe('OpenAI quota exceeded')
    expect(error.diagnostics.some((d) => d.message.includes('OpenAI quota'))).toBe(true)
  })

  it('normalizeTurnFailed exposes a stable local transport failure without exposing the raw payload in the message', () => {
    const error = normalizeTurnFailed({
      message: 'fetch failed',
      cause: { code: 'UND_ERR_HEADERS_TIMEOUT', message: 'Headers Timeout Error' },
    })
    expect(error.message).toContain('UND_ERR_HEADERS_TIMEOUT')
    expect(error.diagnostics.some((d) => d.code === 'opencode-transport-failed')).toBe(true)
  })

  it('normalizeInvalidInput echoes the message', () => {
    const error = normalizeInvalidInput('model must be a string')
    expect(error.kind).toBe('invalid-input')
    expect(error.message).toBe('model must be a string')
  })

  it('normalizeInterrupted is informational', () => {
    const error = normalizeInterrupted()
    expect(error.kind).toBe('interrupted')
    expect(error.diagnostics.some((d) => d.severity === 'info')).toBe(true)
  })
})

describe('OpenCodeRuntime boundary', () => {
  it('only exports Mohist-owned types and helpers (no SDK DTOs)', () => {
    const surface = runtimeModule as Record<string, unknown>
    const forbiddenPrefixes = [
      'OpencodeClient',
      'OpencodeServer',
      'V2Model',
      'V2Provider',
      'ProviderV2',
      'ModelV2',
      'Session2',
      'HeyApi',
    ]
    for (const name of Object.keys(surface)) {
      for (const prefix of forbiddenPrefixes) {
        expect(name.startsWith(prefix)).toBe(false)
      }
    }
  })

  it('does not re-export createOpencodeServer, createOpencodeClient, or OpencodeClient', () => {
    const surface = runtimeModule as Record<string, unknown>
    expect(surface['createOpencodeServer']).toBeUndefined()
    expect(surface['createOpencodeClient']).toBeUndefined()
    expect(surface['OpencodeClient']).toBeUndefined()
    expect(surface['OpencodeServer']).toBeUndefined()
  })
})

describe('OpenCodeRuntime readiness contract', () => {
  it('is not ready before start()', () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    expect(runtime.ready()).toBe(false)
    expect(runtime.diagnostic()).toBeNull()
  })

  it('ready() becomes true after server health and event-subscription setup', async () => {
    const { deps, subscription } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    const start = await runtime.start()
    expect(start.ok).toBe(true)
    expect(runtime.ready()).toBe(true)
    expect(runtime.diagnostic()).toBeNull()
    expect(subscription.subscribeCalls).toBe(1)
  })

  it('stays not ready when the health check fails and surfaces a diagnostic', async () => {
    const { deps } = buildDeps({ failHealth: true })
    const runtime = new OpenCodeRuntime(deps)
    const result = await runtime.start()
    if (result.ok) throw new Error('expected start to fail')
    expect(runtime.ready()).toBe(false)
    expect(result.error.kind).toBe('unavailable-runtime')
    expect(runtime.diagnostic()?.code).toBe('health-failed')
  })

  it('emits an actionable diagnostic when the server cannot start', async () => {
    const { deps } = buildDeps({ failStart: true })
    const runtime = new OpenCodeRuntime(deps)
    const result = await runtime.start()
    if (result.ok) throw new Error('expected start to fail')
    expect(runtime.ready()).toBe(false)
    expect(result.error.kind).toBe('unavailable-runtime')
    expect(runtime.diagnostic()?.code).toBe('server-spawn-failed')
  })

  it('start() is idempotent while ready', async () => {
    const { deps, closed } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    const first = await runtime.start()
    expect(first.ok).toBe(true)
    const second = await runtime.start()
    expect(second.ok).toBe(true)
    expect(closed.value).toBe(false)
  })

  it('shutdown clears readiness and closes the event subscription and server', async () => {
    const { deps, subscription, closed } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    await runtime.shutdown()

    expect(runtime.ready()).toBe(false)
    expect(runtime.diagnostic()).toBeNull()
    expect(subscription.closed).toBe(true)
    expect(closed.value).toBe(true)
  })
})

describe('OpenCodeRuntime on simulated server exit', () => {
  it('fails closed while the old server is still closing after disconnect', async () => {
    const closeGate = deferred<void>()
    const { deps, subscription, closeStarted } = buildDeps({ closeGate: closeGate.promise })
    const runtime = new OpenCodeRuntime(deps)
    const started = await runtime.start()
    expect(started.ok).toBe(true)

    const callback = vi.fn(async () => true)
    subscription.emit({ type: 'server.disconnected', payload: {} })
    await closeStarted

    await expect(runtime.withRemovalFence('/tmp/projA', callback)).resolves.toEqual({ kind: 'failed' })
    expect(callback).not.toHaveBeenCalled()

    closeGate.resolve()
    await runtime.start()
  })

  it('ready() becomes false, in-flight createSession fails, and a background rebuild re-passes readiness', async () => {
    vi.useFakeTimers()
    try {
      const { deps, subscription } = buildDeps({ rebuildDelayMs: 50 })
      const runtime = new OpenCodeRuntime(deps)
      const started = await runtime.start()
      expect(started.ok).toBe(true)
      expect(runtime.ready()).toBe(true)

      const before = await runtime.createSession({
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
      })
      if (!before.ok) throw new Error('expected createSession to succeed before exit')
      expect(before.value.runtimeSessionId).toBe('ses__tmp_projA')

      subscription.emit({ type: 'server.disconnected', payload: {} })

      expect(runtime.ready()).toBe(false)
      expect(runtime.diagnostic()?.code).toBe('server-exit')

      const during = await runtime.createSession({
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
      })
      if (during.ok) throw new Error('expected createSession to fail during rebuild')
      expect(during.error.kind).toBe('unavailable-runtime')

      await vi.advanceTimersByTimeAsync(50)
      expect(runtime.ready()).toBe(true)
      expect(runtime.diagnostic()).toBeNull()

      const after = await runtime.createSession({
        target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projB' },
      })
      if (!after.ok) throw new Error(`expected createSession to succeed after rebuild: ${after.error.kind}`)
      expect(after.value.workDir).toBe('/tmp/projB')
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('OpenCodeRuntime.createSession', () => {
  it('returns a Mohist-owned runtime session id', async () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.createSession({
      target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
    })
    if (!result.ok) throw new Error(`expected createSession to succeed: ${result.error.kind}`)
    expect(result.value.runtimeSessionId).toBe('ses__tmp_projA')
    expect(result.value.workDir).toBe('/tmp/projA')
  })

  it('fails with unavailable-runtime when the runtime is not ready', async () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    const result = await runtime.createSession({
      target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
    })
    if (result.ok) throw new Error('expected createSession to fail')
    expect(result.error.kind).toBe('unavailable-runtime')
  })

  it('normalizes a session-create error as turn-failed with diagnostics', async () => {
    const { deps } = buildDeps({ failSessionCreate: true })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.createSession({
      target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/projA' },
    })
    if (result.ok) throw new Error('expected createSession to fail')
    expect(result.error.kind).toBe('turn-failed')
  })
})

describe('OpenCodeRuntime.resolveSession', () => {
  it('preserves a still-queryable idle binding and reports the active-turn snapshot', async () => {
    const { deps } = buildDeps({ resolveSession: { runtimeSessionId: 'ses_existing', activeTurn: false } })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.resolveSession({
      target: { runtime: 'opencode', runtimeSessionId: 'ses_existing', workDir: '/tmp/work' },
    })
    if (!result.ok) throw new Error(`expected resolveSession to succeed: ${result.error.kind}`)
    expect(result.value).toEqual({ runtimeSessionId: 'ses_existing', workDir: '/tmp/work', activeTurn: false })
  })

  it('reports activeTurn true for a streaming status', async () => {
    const { deps } = buildDeps({ resolveSession: { runtimeSessionId: 'ses_existing', activeTurn: true } })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.resolveSession({
      target: { runtime: 'opencode', runtimeSessionId: 'ses_existing', workDir: '/tmp/work' },
    })
    if (!result.ok) throw new Error(`expected resolveSession to succeed: ${result.error.kind}`)
    expect(result.value.activeTurn).toBe(true)
  })

  it('reattaches to an active session and reads its terminal result without prompting', async () => {
    const { deps, client } = buildDeps({ resolveSession: { runtimeSessionId: 'ses_existing', activeTurn: true } })
    let statusReads = 0
    client.sessionStatus.mockImplementation(async () => {
      statusReads += 1
      return { data: { ses_existing: { type: statusReads === 1 ? 'streaming' : 'idle' } } }
    })
    client.sessionMessages.mockResolvedValue({
      data: [{ type: 'assistant', content: [{ type: 'text', text: 'adopted answer' }] }],
      cursor: {},
    })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.reattachTurn(
      {
        target: { runtime: 'opencode', runtimeSessionId: 'ses_existing', workDir: '/tmp/work' },
      },
      new AbortController().signal,
    )

    expect(result).toMatchObject({ ok: true, value: { facts: { finalAssistantText: 'adopted answer' } } })
    expect(client.sessionMessages).toHaveBeenCalledTimes(1)
  })

  it('classifies a confirmed missing session.get (404) as missing-session', async () => {
    const sessionGet = vi.fn(async () => {
      throw Object.assign(new Error('not found'), { status: 404 })
    })
    const sessionStatus = vi.fn(async () => ({ data: {} }))
    const subscription = new FakeSubscription()
    const closed = { value: false }
    const server: OpencodeServerHandle = {
      url: 'http://fake',
      directory: '/tmp/work',
      client: {
        global: { health: vi.fn(async () => ({ data: { ok: true } })) },
        session: { create: vi.fn(), get: sessionGet, status: sessionStatus },
      } as unknown as OpencodeClient,
      async close() {
        closed.value = true
      },
    }
    const deps: OpenCodeRuntimeDeps = {
      directory: '/tmp/work',
      serverFactory: async () => server,
      eventSubscriptionFactory: () => subscription,
    }
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.resolveSession({
      target: { runtime: 'opencode', runtimeSessionId: 'ses_gone', workDir: '/tmp/work' },
    })
    if (result.ok) throw new Error('expected resolveSession to fail')
    expect(result.error.kind).toBe('missing-session')
    expect(sessionStatus).not.toHaveBeenCalled()
  })

  it('does not classify a status-probe failure as missing-session (keeps the binding, no recovery)', async () => {
    const { deps, client } = buildDeps({ resolveSession: { runtimeSessionId: 'ses_existing', activeTurn: false } })
    client.sessionStatus.mockRejectedValueOnce(Object.assign(new Error('status endpoint missing'), { status: 404 }))
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.resolveSession({
      target: { runtime: 'opencode', runtimeSessionId: 'ses_existing', workDir: '/tmp/work' },
    })
    if (result.ok) throw new Error('expected resolveSession to fail')
    expect(result.error.kind).not.toBe('missing-session')
    expect(result.error.kind).toBe('turn-failed')
  })

  it('classifies a non-404 session.get failure as turn-failed, not missing-session', async () => {
    const { deps } = buildDeps({ failSessionGet: true })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.resolveSession({
      target: { runtime: 'opencode', runtimeSessionId: 'ses_existing', workDir: '/tmp/work' },
    })
    if (result.ok) throw new Error('expected resolveSession to fail')
    expect(result.error.kind).toBe('turn-failed')
  })

  it('fails with unavailable-runtime when the runtime is not ready', async () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    const result = await runtime.resolveSession({
      target: { runtime: 'opencode', runtimeSessionId: 'ses_existing', workDir: '/tmp/work' },
    })
    if (result.ok) throw new Error('expected resolveSession to fail')
    expect(result.error.kind).toBe('unavailable-runtime')
  })

  it('fails with missing-session when no runtimeSessionId is bound', async () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.resolveSession({
      target: { runtime: 'opencode', runtimeSessionId: null, workDir: '/tmp/work' },
    })
    if (result.ok) throw new Error('expected resolveSession to fail')
    expect(result.error.kind).toBe('missing-session')
  })
})

describe('factory seam', () => {
  interface FactoryTestResources {
    fileSystem: RunnerFileSystem
    openCodeRuntimeFactory?: OpenCodeRuntimeFactory
  }

  function factoryIt(name: string, body: (resources: FactoryTestResources) => Promise<void> | void): void {
    it(name, async () => {
      const resources: FactoryTestResources = { fileSystem: new MemoryFileSystem() }
      await withTestRunnerResources(async () => await body(resources), resources)
    })
  }

  factoryIt('an absent scoped factory restores the default factory', (resources) => {
    const defaultFactory = createDefaultOpenCodeRuntime
    resources.openCodeRuntimeFactory = () => {
      throw new Error('should not be called')
    }
    delete resources.openCodeRuntimeFactory
    expect(getOpenCodeRuntimeFactory()).toBe(defaultFactory)
  })

  factoryIt('getOpenCodeRuntimeFactory returns a function that builds an OpenCodeRuntime', () => {
    const factory = getOpenCodeRuntimeFactory()
    const built = factory({
      directory: '/tmp/work',
      serverFactory: async () => {
        throw new Error('not used')
      },
      eventSubscriptionFactory: () => ({
        subscribe() {
          return () => {}
        },
        async close() {},
      }),
    })
    expect(built).toBeInstanceOf(OpenCodeRuntime)
  })

  factoryIt('a scoped factory replaces the default factory', (resources) => {
    const replacement: OpenCodeRuntimeFactory = () => {
      throw new Error('custom factory')
    }
    resources.openCodeRuntimeFactory = replacement
    expect(getOpenCodeRuntimeFactory()).toBe(replacement)
  })
})
