import { describe, expect, it, vi } from 'vitest'
import { AgentJobExecutor } from '../src/runtime/agent-job-executor.js'
import type { AgentJobRuntimeAccessors } from '../src/runtime/agent-job-executor.js'
import {
  buildLostTurnStartUnknown,
  CODEX_CLOSEOUT_WARNING_TEXT,
  driveTurnToCompletion,
  scheduleCloseoutWarning,
  scheduleDeadlineInterrupt,
  submitTurnStart,
} from '../src/runtime/codex/index.js'
import type { CodexClock, CodexTurnTransport } from '../src/runtime/codex/index.js'
import {
  BindingRecoveryCoordinator,
  resolveOrRecoverBinding,
  type RecoverableRuntime,
  type RuntimeBinding,
} from '../src/runtime/binding-recovery.js'
import type { DispatchWorkItem } from '../src/core/types.js'
import type { ServerConnection } from '../src/server/connection.js'
import { makeFakeCodexRuntime } from './support/codex-runtime-fixture.js'
import { verifyOnlyNamedWorkspaceManager } from './support/workspace-mock.js'

/**
 * Cross-cutting fake-only matrix for the Codex runtime.
 *
 * The module-level guarantees (exact-ID routing, completion authority,
 * no-replay, two-phase closeout) live next to the modules; this file
 * exercises the same seams the Pi and OpenCode suites use (an injected
 * fake runtime, a fake app-server transport, an injected clock, and the
 * binding-recovery coordinator) so the required scenarios are pinned in
 * one discoverable place:
 *
 *   - normal execution
 *   - concurrent binding
 *   - event out-of-order delivery
 *   - app-server process loss
 *   - missing recovery
 *   - no-replay
 *   - two-phase closeout
 */

const THREAD_ID = 'thread-matrix'
const TURN_ID = 'turn-matrix'
const WORK_DIR = '/tmp/codex-matrix-ws'

interface RecordedCall {
  readonly method: string
  readonly params: unknown
  readonly id: number
}

interface MatrixTransport extends CodexTurnTransport {
  readonly calls: RecordedCall[]
  readonly denied: Array<{ readonly id: number | string; readonly reason: string }>
  emit(message: unknown): void
  setExited(value: boolean): void
  setResponse(method: string, response: unknown): void
}

function buildTransport(options: { readonly failSend?: Error } = {}): MatrixTransport {
  const calls: RecordedCall[] = []
  const denied: Array<{ id: number | string; reason: string }> = []
  const listeners = new Set<(message: unknown) => void>()
  const responses = new Map<string, unknown>([
    ['turn/start', { jsonrpc: '2.0', id: 1, result: { threadId: THREAD_ID, turnId: TURN_ID, status: 'inProgress' } }],
    ['turn/interrupt', { jsonrpc: '2.0', id: 2, result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true } }],
    ['turn/steer', { jsonrpc: '2.0', id: 3, result: { accepted: true } }],
  ])
  let exited = false
  return {
    calls,
    denied,
    async send(request) {
      calls.push({ method: request.method, params: request.params, id: request.id })
      if (options.failSend) throw options.failSend
      const response = responses.get(request.method)
      if (response === undefined) throw new Error(`unexpected method ${request.method}`)
      return response as never
    },
    denyServerRequest(id, reason) {
      denied.push({ id, reason })
    },
    subscribe(listener) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    hasExited: () => exited,
    setExited(value) {
      exited = value
    },
    setResponse(method, response) {
      responses.set(method, response)
    },
    emit(message) {
      for (const listener of listeners) listener(message)
    },
  }
}

function makeNextRequestId(): () => number {
  let next = 10
  return () => {
    const id = next
    next += 1
    return id
  }
}

/**
 * Deterministic clock for the two-phase closeout tests. No real timers
 * are used; `advance` fires the due callbacks synchronously.
 */
function makeManualClock(): { readonly clock: CodexClock; advance(ms: number): void } {
  let now = 0
  let nextHandle = 1
  const timers: Array<{ readonly handle: number; readonly at: number; readonly callback: () => void }> = []
  return {
    clock: {
      now: () => now,
      setTimeout: (callback, delayMs) => {
        const handle = nextHandle
        nextHandle += 1
        timers.push({ handle, at: now + Math.max(0, delayMs), callback })
        return handle
      },
      clearTimeout: (handle) => {
        const index = timers.findIndex((timer) => timer.handle === handle)
        if (index >= 0) timers.splice(index, 1)
      },
    },
    advance(ms) {
      now += ms
      const due = timers.filter((timer) => timer.at <= now).sort((a, b) => a.at - b.at)
      for (const timer of due) {
        const index = timers.indexOf(timer)
        if (index >= 0) timers.splice(index, 1)
        timer.callback()
      }
    },
  }
}

interface FakeConnectionHandles {
  readonly connection: ServerConnection
  readonly attachCalls: Array<{ projectId: string; sessionId: string; body: Record<string, unknown> }>
}

function makeFakeConnection(): FakeConnectionHandles {
  const attachCalls: FakeConnectionHandles['attachCalls'] = []
  const connection = {
    runnerId: 'runner-1',
    async openAgentSession() {},
    async attachAgentSession(projectId: string, sessionId: string, body: Record<string, unknown>) {
      attachCalls.push({ projectId, sessionId, body })
    },
    async getAgentSession(_projectId: string, sessionId: string) {
      return { sessionId, runtime: 'codex', runtimeSessionId: null, workDir: WORK_DIR }
    },
    async agentSessionRuntimeEvents() {},
  } as unknown as ServerConnection
  return { connection, attachCalls }
}

function buildWork(overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: '',
    workId: 'aj-codex-matrix',
    workType: 'task',
    ownerKind: 'agent-job',
    agentJobId: 'aj-codex-matrix',
    agentSessionId: 'session-1',
    projectId: 'proj-1',
    with: { prompt: 'run the matrix', runtime: 'codex', executionSource: 'non-slack' },
    variables: {
      workspace: { name: 'issue-9', branch: null, changeDir: null },
      repository: { name: 'master', gitUrl: 'https://example.test/repository.git', baseBranch: 'master' },
    },
    ...overrides,
  }
}

function makeExecutor(connection: ServerConnection, accessors: AgentJobRuntimeAccessors): AgentJobExecutor {
  return new AgentJobExecutor(
    connection,
    accessors,
    null,
    undefined,
    verifyOnlyNamedWorkspaceManager({ path: WORK_DIR, branch: null }),
  )
}

describe('Codex cross-cutting matrix: normal execution', () => {
  it('completes a codex turn through the shared fixture and binds the AgentSession', async () => {
    const { connection, attachCalls } = makeFakeConnection()
    const fixture = makeFakeCodexRuntime()
    const executor = makeExecutor(connection, { openCode: null, pi: null, codex: fixture.runtime })

    const result = await executor.execute(
      buildWork({ initialInputId: 'input-1', initialTurnId: 'turn-1' }),
      new AbortController().signal,
    )

    expect(result.status).toBe('completed')
    expect(fixture.runTurnCalls).toHaveLength(1)
    expect(attachCalls).toHaveLength(1)
    expect((result.output as Record<string, unknown>).kind).toBe('codex')
    expect(result.agentBinding).toMatchObject({ runtime: 'codex', runtimeSessionId: 'thread_fixture' })
  })
})

describe('Codex cross-cutting matrix: concurrent binding', () => {
  it('coalesces concurrent recovery attempts into one replacement Thread and one binding CAS', async () => {
    const fixture = makeFakeCodexRuntime()
    const coordinator = new BindingRecoveryCoordinator()
    const recoverable: RecoverableRuntime = { kind: 'codex', runtime: fixture.runtime }
    const expected: RuntimeBinding = {
      runnerId: 'runner-1',
      runtime: 'codex',
      runtimeSessionId: 'thread-stale',
      workDir: '/work',
    }
    const probe = vi.fn(async () => ({ ok: false as const, kind: 'missing-session', message: 'thread_not_found' }))
    const replace = vi.fn(async () => undefined)
    const request = {
      runnerId: 'runner-1',
      expected,
      runtime: recoverable,
      probe,
      replace,
      coordinator,
      recoveryKey: 'session-1:thread-stale',
    }

    const [first, second] = await Promise.all([resolveOrRecoverBinding(request), resolveOrRecoverBinding(request)])

    expect(first).toEqual(second)
    expect(first).toMatchObject({ ok: true, recovered: true })
    expect(probe).toHaveBeenCalledOnce()
    expect(fixture.createSessionCalls).toHaveLength(1)
    expect(replace).toHaveBeenCalledOnce()
  })
})

describe('Codex cross-cutting matrix: event out-of-order delivery', () => {
  it('does not complete early on out-of-order item events and completes only on the exact terminal event', async () => {
    const transport = buildTransport()
    const observed: string[] = []
    const completion = driveTurnToCompletion({
      transport,
      runtimeSessionId: THREAD_ID,
      workDir: WORK_DIR,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: null,
      nextRequestId: makeNextRequestId(),
      observer: {
        onEvent: (event) => observed.push(event.type),
        onDiagnostic: (diagnostic) => observed.push(`diagnostic:${diagnostic.code}`),
      },
    })

    // Items arrive before the turn-completed signal and some carry a
    // different Turn ID. None of them may complete the Turn.
    transport.emit({ type: 'agentMessage', threadId: THREAD_ID, turnId: TURN_ID, text: 'partial', delta: true })
    transport.emit({ type: 'agentMessage', threadId: THREAD_ID, turnId: 'other-turn', text: 'stale' })
    transport.emit({ type: 'thread/status', threadId: THREAD_ID, turnId: TURN_ID, status: 'idle' })
    transport.emit({ type: 'turn/completed', threadId: THREAD_ID, turnId: 'other-turn', status: 'completed' })

    transport.emit({ type: 'agentMessage', threadId: THREAD_ID, turnId: TURN_ID, text: 'final', delta: false })
    transport.emit({ type: 'turn/completed', threadId: THREAD_ID, turnId: TURN_ID, status: 'completed' })

    const result = await completion
    expect(result).toMatchObject({ ok: true, value: { facts: { runtimeSessionId: THREAD_ID } } })
    expect(observed).toContain('message.delta')
    expect(observed.some((entry) => entry.startsWith('diagnostic:item-stale'))).toBe(true)
  })
})

describe('Codex cross-cutting matrix: app-server process loss', () => {
  it('surfaces unknown when the child exits before the turn/start response and never replays the input', async () => {
    const transport = buildTransport({ failSend: new Error('app-server child exited') })
    transport.setExited(true)

    const submission = {
      threadId: THREAD_ID,
      workDir: WORK_DIR,
      prompt: 'lost turn',
      fileParts: null,
      clientUserMessageId: 'sess_input_lost',
      resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
    }
    const first = await submitTurnStart(transport, submission, 4)
    expect(first).toMatchObject({ ok: false, error: { kind: 'unknown' } })

    // The same Input is never resubmitted, even with the same
    // clientUserMessageId.
    const lost = buildLostTurnStartUnknown({
      threadId: THREAD_ID,
      clientUserMessageId: submission.clientUserMessageId,
      workDir: WORK_DIR,
    })
    expect(lost).toMatchObject({ ok: false, error: { kind: 'unknown' } })
    expect(transport.calls.filter((call) => call.method === 'turn/start')).toHaveLength(1)
  })
})

describe('Codex cross-cutting matrix: missing recovery', () => {
  it('creates exactly one replacement Thread after structured thread_not_found and CASes the binding', async () => {
    const fixture = makeFakeCodexRuntime()
    fixture.setCreateSessionResult({
      ok: true,
      value: { runtimeSessionId: 'thread_fixture_created', workDir: '/work' },
      diagnostics: [],
    })
    const replace = vi.fn(async () => undefined)
    const expected: RuntimeBinding = {
      runnerId: 'runner-1',
      runtime: 'codex',
      runtimeSessionId: 'thread-stale',
      workDir: '/work',
    }

    const result = await resolveOrRecoverBinding({
      runnerId: 'runner-1',
      expected,
      runtime: { kind: 'codex', runtime: fixture.runtime },
      probe: async () => ({ ok: false as const, kind: 'missing-session', message: 'thread_not_found' }),
      replace,
    })

    expect(result).toMatchObject({ ok: true, recovered: true })
    expect(fixture.createSessionCalls).toHaveLength(1)
    expect(replace).toHaveBeenCalledWith(expected, {
      ...expected,
      runtimeSessionId: 'thread_fixture_created',
    })
  })

  it('treats transport, authentication, and protocol failures as unknown without replacing the binding', async () => {
    const cases = [
      ['transport failure', 'unknown'],
      ['authentication failure', 'unavailable-runtime'],
      ['protocol mismatch', 'incompatible-runtime'],
    ] as const

    for (const [label, kind] of cases) {
      const fixture = makeFakeCodexRuntime()
      const replace = vi.fn(async () => undefined)
      const result = await resolveOrRecoverBinding({
        runnerId: 'runner-1',
        expected: {
          runnerId: 'runner-1',
          runtime: 'codex',
          runtimeSessionId: 'thread-stale',
          workDir: '/work',
        },
        runtime: { kind: 'codex', runtime: fixture.runtime },
        probe: async () => ({ ok: false as const, kind, message: label }),
        replace,
      })

      expect(result).toMatchObject({ ok: false, kind })
      expect(fixture.createSessionCalls).toHaveLength(0)
      expect(replace).not.toHaveBeenCalled()
    }
  })
})

describe('Codex cross-cutting matrix: no-replay', () => {
  it('never resubmits the same SessionInput ID after a lost turn/start', async () => {
    const transport = buildTransport({ failSend: new Error('write failed after submission may have occurred') })
    const submission = {
      threadId: THREAD_ID,
      workDir: WORK_DIR,
      prompt: 'hello',
      fileParts: null,
      clientUserMessageId: 'sess_input_42',
      resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
    }

    const first = await submitTurnStart(transport, submission, 4)
    expect(first).toMatchObject({ ok: false, error: { kind: 'unknown' } })
    const callsAfterFirst = transport.calls.length

    // The runtime reports `unknown`; it does not invoke the transport.
    buildLostTurnStartUnknown({
      threadId: THREAD_ID,
      clientUserMessageId: submission.clientUserMessageId,
      workDir: WORK_DIR,
    })
    buildLostTurnStartUnknown({
      threadId: THREAD_ID,
      clientUserMessageId: submission.clientUserMessageId,
      workDir: WORK_DIR,
    })

    expect(transport.calls.length).toBe(callsAfterFirst)
    expect(transport.calls.filter((call) => call.method === 'turn/start')).toHaveLength(1)
  })
})

describe('Codex cross-cutting matrix: two-phase closeout', () => {
  it('sends the locked closeout warning on the exact active Turn at the lead time', () => {
    const transport = buildTransport()
    const manual = makeManualClock()
    const handle = scheduleCloseoutWarning({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: 1000,
      clock: manual.clock,
      nextRequestId: makeNextRequestId(),
    })

    expect(handle).toMatchObject({ scheduled: true, fired: false, effectiveDelayMs: 0 })
    manual.advance(0)
    expect(handle.fired).toBe(true)
    const steer = transport.calls.find((call) => call.method === 'turn/steer')
    expect(steer).toBeDefined()
    expect(steer?.params).toMatchObject({
      threadId: THREAD_ID,
      expectedTurnId: TURN_ID,
      input: [{ type: 'text', text: CODEX_CLOSEOUT_WARNING_TEXT, text_elements: [] }],
    })
    handle.dispose()
  })

  it('fixes the deadline interrupt and confirms it only on the matching terminal event', async () => {
    const transport = buildTransport()
    const manual = makeManualClock()
    const handle = scheduleDeadlineInterrupt({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: 1000,
      clock: manual.clock,
      nextRequestId: makeNextRequestId(),
    })

    const confirmation = handle.awaitConfirmation()
    manual.advance(1000)
    expect(handle.fired).toBe(true)
    expect(transport.calls.find((call) => call.method === 'turn/interrupt')).toMatchObject({
      params: { threadId: THREAD_ID, turnId: TURN_ID },
    })

    // A terminal event for a different Turn must not confirm.
    transport.emit({ type: 'turn/completed', threadId: THREAD_ID, turnId: 'other-turn', status: 'interrupted' })
    transport.emit({ type: 'turn/completed', threadId: THREAD_ID, turnId: TURN_ID, status: 'interrupted' })
    await expect(confirmation).resolves.toEqual({ status: 'confirmed' })
    handle.dispose()
  })

  it('reports budget-exhausted when no matching terminal event arrives', async () => {
    const transport = buildTransport()
    const manual = makeManualClock()
    const handle = scheduleDeadlineInterrupt({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: 1000,
      clock: manual.clock,
      nextRequestId: makeNextRequestId(),
    })

    const confirmation = handle.awaitConfirmation()
    manual.advance(1000)
    manual.advance(60_000)
    await expect(confirmation).resolves.toEqual({ status: 'budget-exhausted' })
    handle.dispose()
  })
})
