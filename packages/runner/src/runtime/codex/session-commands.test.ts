import { describe, expect, it, vi } from 'vitest'
import { CodexRuntime } from './runtime.js'
import type { CodexServerHandle } from './server-process.js'
import type { CodexAuthenticationProbe, CodexCatalogLoader, CodexCliProbe, CodexReadinessProbe } from './readiness.js'
import { CODEX_DEFAULT_TIMEOUTS } from './types.js'

const MANAGED_CODEX_HOME = '/runner/.mohist/codex'
const WORK_DIR = '/work'

interface SendRecord {
  readonly method: string
  readonly params: unknown
  readonly id: number
}

interface FakeServerHandle extends CodexServerHandle {
  readonly sends: SendRecord[]
  readonly methods: string[]
  emit(message: unknown): void
}

function passingProbe(): CodexReadinessProbe {
  const cli: CodexCliProbe = {
    async resolveCodexBinary() {
      return '/usr/local/bin/codex'
    },
    async resolveCodexVersion() {
      return '0.153.0'
    },
  }
  const authentication: CodexAuthenticationProbe = {
    async hasManagedAuthentication() {
      return true
    },
  }
  const catalog: CodexCatalogLoader = {
    async loadCatalog() {
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

/**
 * Fake app-server handle whose Turn lifecycle is driven by explicit
 * `emit` calls so a test can observe the exact `turn/start`,
 * `turn/steer`, `turn/interrupt`, and `thread/compact/start` traffic
 * without a real child process.
 */
function fakeServerHandle(): FakeServerHandle {
  const sends: SendRecord[] = []
  const methods: string[] = []
  const listeners = new Set<(message: unknown) => void>()
  let threadOrdinal = 0
  let turnOrdinal = 0
  const emit = (message: unknown): void => {
    for (const listener of listeners) listener(message)
  }
  const handle: FakeServerHandle = {
    codexHome: MANAGED_CODEX_HOME,
    sends,
    methods,
    emit,
    async send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R> {
      sends.push({ method: request.method, params: request.params, id: request.id })
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
          const cwd = (request.params as { readonly cwd?: string } | undefined)?.cwd ?? WORK_DIR
          return { jsonrpc: '2.0', id: request.id, result: { threadId, cwd } } as unknown as R
        }
        case 'thread/resume': {
          const params = request.params as { readonly threadId?: string; readonly cwd?: string } | undefined
          return {
            jsonrpc: '2.0',
            id: request.id,
            result: { threadId: params?.threadId ?? 'thread-1', cwd: params?.cwd ?? WORK_DIR },
          } as unknown as R
        }
        case 'turn/start': {
          const params = request.params as { readonly threadId?: string } | undefined
          const turnId = `turn-${++turnOrdinal}`
          return {
            jsonrpc: '2.0',
            id: request.id,
            result: { threadId: params?.threadId ?? 'thread-1', turnId, status: 'inProgress' },
          } as unknown as R
        }
        case 'turn/steer':
        case 'turn/interrupt':
          return { jsonrpc: '2.0', id: request.id, result: { accepted: true } } as unknown as R
        case 'thread/compact/start': {
          const params = request.params as { readonly threadId?: string } | undefined
          const threadId = params?.threadId ?? 'thread-1'
          const turnId = `compact-${++turnOrdinal}`
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
    subscribe(listener) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    async close() {
      listeners.clear()
    },
  }
  return handle
}

async function flushMicrotasks(times = 6): Promise<void> {
  for (let index = 0; index < times; index += 1) await Promise.resolve()
}

async function startedRuntime(handle: FakeServerHandle): Promise<CodexRuntime> {
  const runtime = new CodexRuntime({
    codexHome: MANAGED_CODEX_HOME,
    cwd: WORK_DIR,
    serverFactory: async () => handle,
    readinessProbe: passingProbe(),
  })
  await expect(runtime.start()).resolves.toMatchObject({ ok: true })
  return runtime
}

describe('Codex follow-up steer routing', () => {
  it('steers the exact active Turn with the frozen expectedTurnId and does not start a new Turn', async () => {
    const handle = fakeServerHandle()
    const runtime = await startedRuntime(handle)
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'codex', runtimeSessionId: null, workDir: WORK_DIR },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
      },
      new AbortController().signal,
    )
    await flushMicrotasks()
    expect(handle.methods.filter((method) => method === 'turn/start')).toHaveLength(1)

    const followup = await runtime.followup({
      target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: WORK_DIR },
      prompt: 'continue',
      clientUserMessageId: 'input-2',
    })
    expect(followup).toMatchObject({ ok: true })

    const steer = handle.sends.find((send) => send.method === 'turn/steer')
    expect(steer?.params).toMatchObject({ threadId: 'thread-1', expectedTurnId: 'turn-1' })
    expect(handle.methods.filter((method) => method === 'turn/start')).toHaveLength(1)

    handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    await expect(turnPromise).resolves.toMatchObject({ ok: true })
    await runtime.shutdown({ clearDiagnostic: true })
  })

  it('starts a new turn/start on the same Thread when no Turn is active', async () => {
    const handle = fakeServerHandle()
    const runtime = await startedRuntime(handle)

    const followupPromise = runtime.followup({
      target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: WORK_DIR },
      prompt: 'first',
      clientUserMessageId: 'input-1',
    })
    await flushMicrotasks()
    const start = handle.sends.find((send) => send.method === 'turn/start')
    expect(start?.params).toMatchObject({ threadId: 'thread-1' })
    expect(handle.methods.filter((method) => method === 'turn/start')).toHaveLength(1)
    expect(handle.methods).not.toContain('turn/steer')

    handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    await expect(followupPromise).resolves.toMatchObject({ ok: true })
    await runtime.shutdown({ clearDiagnostic: true })
  })

  it('rejects a Codex follow-up without the caller inputId', async () => {
    const handle = fakeServerHandle()
    const runtime = await startedRuntime(handle)
    const result = await runtime.followup({
      target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: WORK_DIR },
      prompt: 'continue',
    })
    expect(result).toMatchObject({ ok: false, error: { kind: 'invalid-input' } })
    await runtime.shutdown({ clearDiagnostic: true })
  })
})

describe('Codex stop confirmation', () => {
  it('does not report a confirmed stop from interrupt acceptance alone', async () => {
    const handle = fakeServerHandle()
    const runtime = await startedRuntime(handle)
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'codex', runtimeSessionId: null, workDir: WORK_DIR },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
      },
      new AbortController().signal,
    )
    await flushMicrotasks()

    vi.useFakeTimers()
    try {
      const cancelPromise = runtime.cancel({
        target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: WORK_DIR },
      })
      await flushMicrotasks()
      expect(handle.methods).toContain('turn/interrupt')

      // The RPC accepted the interrupt but no matching terminal event
      // arrived: the bounded budget expires and the stop stays
      // unconfirmed.
      await vi.advanceTimersByTimeAsync(CODEX_DEFAULT_TIMEOUTS.cancelConfirmationMs)
      await expect(cancelPromise).resolves.toMatchObject({
        ok: true,
        value: { facts: { cancelled: true, stopConfirmed: false } },
      })

      handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'interrupted' })
      await turnPromise
    } finally {
      vi.useRealTimers()
    }
    await runtime.shutdown({ clearDiagnostic: true })
  })

  it('confirms the stop only after the matching terminal event', async () => {
    const handle = fakeServerHandle()
    const runtime = await startedRuntime(handle)
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'codex', runtimeSessionId: null, workDir: WORK_DIR },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
      },
      new AbortController().signal,
    )
    await flushMicrotasks()

    const cancelPromise = runtime.cancel({
      target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: WORK_DIR },
    })
    await flushMicrotasks()
    handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'interrupted' })

    await expect(cancelPromise).resolves.toMatchObject({
      ok: true,
      value: { facts: { cancelled: true, stopConfirmed: true } },
    })
    await expect(turnPromise).resolves.toMatchObject({ ok: false, error: { kind: 'interrupted' } })
    await runtime.shutdown({ clearDiagnostic: true })
  })

  it('ignores a terminal event for a different Turn when confirming a stop', async () => {
    const handle = fakeServerHandle()
    const runtime = await startedRuntime(handle)
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'codex', runtimeSessionId: null, workDir: WORK_DIR },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
      },
      new AbortController().signal,
    )
    await flushMicrotasks()

    vi.useFakeTimers()
    try {
      const cancelPromise = runtime.cancel({
        target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: WORK_DIR },
      })
      await flushMicrotasks()
      handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-other', status: 'interrupted' })
      await vi.advanceTimersByTimeAsync(CODEX_DEFAULT_TIMEOUTS.cancelConfirmationMs)
      await expect(cancelPromise).resolves.toMatchObject({
        value: { facts: { stopConfirmed: false } },
      })

      handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'interrupted' })
      await turnPromise
    } finally {
      vi.useRealTimers()
    }
    await runtime.shutdown({ clearDiagnostic: true })
  })
})

describe('Codex compact idle gate', () => {
  it('rejects compact while a Turn is active without sending thread/compact/start', async () => {
    const handle = fakeServerHandle()
    const runtime = await startedRuntime(handle)
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'codex', runtimeSessionId: null, workDir: WORK_DIR },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
      },
      new AbortController().signal,
    )
    await flushMicrotasks()

    const result = await runtime.compact({
      target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: WORK_DIR },
    })
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
    expect(handle.methods).not.toContain('thread/compact/start')

    handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    await turnPromise
    await runtime.shutdown({ clearDiagnostic: true })
  })

  it('fails compact when the matching Turn completes without a contextCompaction item', async () => {
    const handle = fakeServerHandle()
    const runtime = await startedRuntime(handle)
    // Establish a known idle Thread without an active Turn.
    const setupPromise = runtime.runTurn(
      {
        target: { runtime: 'codex', runtimeSessionId: null, workDir: WORK_DIR },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
      },
      new AbortController().signal,
    )
    await flushMicrotasks()
    handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    await setupPromise

    const compactPromise = runtime.compact({
      target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: WORK_DIR },
    })
    await flushMicrotasks()
    // The deprecated `thread/compacted` notification is not completion
    // authority: only the matching turn/completed confirms, and the
    // missing contextCompaction item makes the compaction fail.
    handle.emit({ type: 'thread/compacted', threadId: 'thread-1' })
    handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'compact-2', status: 'completed' })
    await expect(compactPromise).resolves.toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
    await runtime.shutdown({ clearDiagnostic: true })
  })
})

describe('Codex reset', () => {
  it('creates an empty Thread in the same workDir and returns the replacement id', async () => {
    const handle = fakeServerHandle()
    const runtime = await startedRuntime(handle)
    const setupPromise = runtime.runTurn(
      {
        target: { runtime: 'codex', runtimeSessionId: null, workDir: WORK_DIR },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
      },
      new AbortController().signal,
    )
    await flushMicrotasks()
    handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    await setupPromise

    const result = await runtime.reset({
      target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: WORK_DIR },
    })
    expect(result).toMatchObject({ ok: true, value: { facts: { runtimeSessionId: 'thread-2', workDir: WORK_DIR } } })
    const startCalls = handle.sends.filter((send) => send.method === 'thread/start')
    expect(startCalls).toHaveLength(2)
    expect(startCalls[1]?.params).toMatchObject({ cwd: WORK_DIR })
    await runtime.shutdown({ clearDiagnostic: true })
  })

  it('rejects reset while the bound Thread is active', async () => {
    const handle = fakeServerHandle()
    const runtime = await startedRuntime(handle)
    const turnPromise = runtime.runTurn(
      {
        target: { runtime: 'codex', runtimeSessionId: null, workDir: WORK_DIR },
        prompt: 'hello',
        clientUserMessageId: 'input-1',
      },
      new AbortController().signal,
    )
    await flushMicrotasks()

    const result = await runtime.reset({
      target: { runtime: 'codex', runtimeSessionId: 'thread-1', workDir: WORK_DIR },
    })
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })

    handle.emit({ type: 'turn/completed', threadId: 'thread-1', turnId: 'turn-1', status: 'completed' })
    await turnPromise
    await runtime.shutdown({ clearDiagnostic: true })
  })
})
