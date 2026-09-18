import { describe, expect, it } from 'vitest'
import { verifyOnlyNamedWorkspaceManager } from './support/workspace-mock.js'
import { AgentJobExecutor } from '../src/runtime/agent-job-executor.js'
import type { AgentJobRuntimeAccessors } from '../src/runtime/agent-job-executor.js'
import { projectCodexTurnToWorkItemResult } from '../src/runtime/agent-job-turn.js'
import type {
  CodexDiagnostic,
  CodexRuntime,
  CodexResult,
  CodexRuntimeTurnEvent,
  CodexTurnEventObserver,
  CodexTurnRequest,
  CodexTurnResult,
} from '../src/runtime/codex/index.js'
import type { ManagerExecutionBoundary } from '../src/runtime/manager-execution-boundary.js'
import type { ServerConnection } from '../src/server/connection.js'
import type { DispatchWorkItem } from '../src/core/types.js'

const THREAD_ID = 'thread-fixture'
const WORK_DIR = '/tmp/agent-job-ws'

interface FakeCodexRuntimeHandles {
  runtime: CodexRuntime
  runTurnCalls: CodexTurnRequest[]
  setReady: (ready: boolean) => void
  setDiagnostic: (diagnostic: CodexDiagnostic | null) => void
  setResult: (result: CodexResult<CodexTurnResult>) => void
  setEvents: (events: readonly CodexRuntimeTurnEvent[]) => void
  setDiagnostics: (diagnostics: readonly CodexDiagnostic[]) => void
  setEmitSessionReady: (emit: boolean) => void
}

/**
 * Fake Codex runtime that reproduces the real module's binding-before-effect
 * ordering: `onSessionReady` (which the executor turns into the durable
 * AgentSession attach + Input identity) resolves before the fake reports the
 * turn as submitted. The runtime deliberately never exposes a `turn/start`
 * seam to the executor — the only observable ordering is the observer calls.
 */
function makeFakeCodexRuntime(order: string[]): FakeCodexRuntimeHandles {
  const runTurnCalls: CodexTurnRequest[] = []
  let ready = true
  let diagnostic: CodexDiagnostic | null = null
  let events: readonly CodexRuntimeTurnEvent[] = []
  let diagnostics: readonly CodexDiagnostic[] = []
  let emitSessionReady = true
  let nextResult: CodexResult<CodexTurnResult> = {
    ok: true,
    value: {
      facts: { finalAssistantText: 'codex finished', runtimeSessionId: THREAD_ID, workDir: WORK_DIR },
      diagnostics: [],
    },
    diagnostics: [],
  }
  const runtime = {
    ready: () => ready,
    diagnostic: () => diagnostic,
    catalog: () => null,
    async runTurn(
      request: CodexTurnRequest,
      _signal?: AbortSignal,
      observer?: CodexTurnEventObserver,
    ): Promise<CodexResult<CodexTurnResult>> {
      runTurnCalls.push(request)
      if (emitSessionReady) {
        await observer?.onSessionReady?.({ runtimeSessionId: THREAD_ID, workDir: request.target.workDir })
      }
      for (const event of events) observer?.onEvent?.(event)
      for (const entry of diagnostics) observer?.onDiagnostic?.(entry)
      order.push('turn/start')
      return nextResult
    },
  } as unknown as CodexRuntime
  return {
    runtime,
    runTurnCalls,
    setReady(value) {
      ready = value
    },
    setDiagnostic(value) {
      diagnostic = value
    },
    setResult(value) {
      nextResult = value
    },
    setEvents(value) {
      events = value
    },
    setDiagnostics(value) {
      diagnostics = value
    },
    setEmitSessionReady(value) {
      emitSessionReady = value
    },
  }
}

interface FakeConnectionHandles {
  connection: ServerConnection
  attachCalls: Array<{ projectId: string; sessionId: string; body: Record<string, unknown> }>
  eventCalls: Array<{ projectId: string; sessionId: string; body: Record<string, unknown> }>
  order: string[]
  setAgentSession: (session: { runtime: string; runtimeSessionId: string | null } | null) => void
}

function makeFakeConnection(order: string[]): FakeConnectionHandles {
  const attachCalls: FakeConnectionHandles['attachCalls'] = []
  const eventCalls: FakeConnectionHandles['eventCalls'] = []
  let agentSession: { runtime: string; runtimeSessionId: string | null } | null = {
    runtime: 'codex',
    runtimeSessionId: null,
  }
  const connection = {
    runnerId: 'runner-1',
    async openAgentSession() {
      order.push('open')
    },
    async attachAgentSession(projectId: string, sessionId: string, body: Record<string, unknown>) {
      attachCalls.push({ projectId, sessionId, body })
      order.push('attach')
    },
    async getAgentSession(_projectId: string, sessionId: string) {
      if (agentSession === null) return null
      return {
        sessionId,
        runtime: agentSession.runtime,
        runtimeSessionId: agentSession.runtimeSessionId,
        workDir: '/tmp/ws',
      }
    },
    async agentSessionRuntimeEvents(projectId: string, sessionId: string, body: Record<string, unknown>) {
      eventCalls.push({ projectId, sessionId, body })
      const first = (body.runtimeEvents as Array<{ type: string }> | undefined)?.[0]
      order.push(first?.type === 'session.input' ? 'input' : `event:${first?.type ?? 'unknown'}`)
    },
  } as unknown as ServerConnection
  return {
    connection,
    attachCalls,
    eventCalls,
    order,
    setAgentSession(session) {
      agentSession = session
    },
  }
}

function buildAgentJobWork(overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: '',
    workId: 'aj-codex',
    workType: 'task',
    ownerKind: 'agent-job',
    agentJobId: 'aj-codex',
    agentSessionId: 'session-1',
    projectId: 'proj-1',
    with: { prompt: 'do the codex thing', runtime: 'codex', executionSource: 'non-slack' },
    variables: {
      workspace: { name: 'issue-9', branch: null, changeDir: null },
      repository: { name: 'master', gitUrl: 'https://example.test/repository.git', baseBranch: 'master' },
    },
    ...overrides,
  }
}

function makeExecutor(
  connection: ServerConnection,
  accessors: AgentJobRuntimeAccessors,
  workDir: string | null = null,
): AgentJobExecutor {
  return new AgentJobExecutor(
    connection,
    accessors,
    workDir,
    undefined,
    verifyOnlyNamedWorkspaceManager({ path: WORK_DIR, branch: null }),
  )
}

function turnEvents(body: Record<string, unknown>): Array<{ type: string; payload: Record<string, unknown> }> {
  return (body.runtimeEvents as Array<{ type: string; payload: Record<string, unknown> }>) ?? []
}

describe('AgentJobExecutor dispatches the Codex runtime', () => {
  it('routes runtime: codex to executeCodexTurn and labels the terminal output as codex', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    const result = await executor.execute(
      buildAgentJobWork({
        initialTurnId: 'turn-1',
        with: { prompt: 'ship on codex', runtime: 'codex', executionSource: 'non-slack' },
      }),
      new AbortController().signal,
    )

    expect(result.status).toBe('completed')
    expect(codex.runTurnCalls).toHaveLength(1)
    expect(codex.runTurnCalls[0].target.runtime).toBe('codex')
    expect(codex.runTurnCalls[0].target.workDir).toBe(WORK_DIR)
    const output = result.output as Record<string, unknown>
    expect(output.kind).toBe('codex')
    expect(output.status).toBe('success')
    expect(output.runtimeSessionId).toBe(THREAD_ID)
    expect(result.agentBinding).toEqual({
      agentSessionId: 'session-1',
      agentTurnId: 'turn-1',
      runtime: 'codex',
      runtimeSessionId: THREAD_ID,
    })
  })

  it('keeps any runtime other than codex/pi/opencode on the invalid-input error', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    const result = await executor.execute(
      buildAgentJobWork({ with: { prompt: 'bad runtime', runtime: 'mystery', executionSource: 'non-slack' } }),
      new AbortController().signal,
    )

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('invalid-input')
    expect(codex.runTurnCalls).toHaveLength(0)
  })

  it('persists the AgentSession binding before the Codex turn is submitted', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    const result = await executor.execute(
      buildAgentJobWork({
        initialInputId: 'input-1',
        initialTurnId: 'turn-1',
        with: { prompt: 'bind first', runtime: 'codex', executionSource: 'non-slack' },
      }),
      new AbortController().signal,
    )

    expect(result.status).toBe('completed')
    expect(connection.attachCalls).toHaveLength(1)
    expect(connection.attachCalls[0].body).toMatchObject({ runtimeSessionId: THREAD_ID, workDir: WORK_DIR })
    // thread/start → onSessionReady (attach) → turn/start.
    expect(order.indexOf('attach')).toBeGreaterThanOrEqual(0)
    expect(order.indexOf('turn/start')).toBeGreaterThan(order.indexOf('attach'))
  })

  it('publishes the Input identity before the Codex turn is submitted when the runner owns it', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    await executor.execute(
      buildAgentJobWork({
        initialInputId: 'input-1',
        with: { prompt: 'publish input', runtime: 'codex', executionSource: 'non-slack' },
      }),
      new AbortController().signal,
    )

    const inputCalls = connection.eventCalls.filter((call) =>
      turnEvents(call.body).some((event) => event.type === 'session.input'),
    )
    expect(inputCalls).toHaveLength(1)
    expect(order.indexOf('input')).toBeLessThan(order.indexOf('turn/start'))
  })

  it('rejects a Codex variant as unsupported-execution-configuration before submitting the turn', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    const result = await executor.execute(
      buildAgentJobWork({
        initialTurnId: 'turn-1',
        with: { prompt: 'no variants', runtime: 'codex', variant: 'fast', executionSource: 'non-slack' },
      }),
      new AbortController().signal,
    )

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('unsupported-execution-configuration')
    expect(codex.runTurnCalls).toHaveLength(0)
  })

  it('passes the model and canonical reasoning effort through to the Codex turn', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    await executor.execute(
      buildAgentJobWork({
        initialTurnId: 'turn-1',
        with: {
          prompt: 'configured turn',
          runtime: 'codex',
          model: 'gpt-5-codex',
          reasoningEffort: 'high',
          variant: null,
          executionSource: 'non-slack',
        },
      }),
      new AbortController().signal,
    )

    expect(codex.runTurnCalls).toHaveLength(1)
    expect(codex.runTurnCalls[0].options).toMatchObject({
      model: 'gpt-5-codex',
      reasoningEffort: 'high',
      variant: null,
    })
  })

  it('does not parse a Codex model as an OpenCode provider/model pair', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    const result = await executor.execute(
      buildAgentJobWork({
        initialTurnId: 'turn-1',
        with: { prompt: 'opaque model id', runtime: 'codex', model: 'gpt-5-codex', executionSource: 'non-slack' },
      }),
      new AbortController().signal,
    )

    expect(result.status).toBe('completed')
    expect(codex.runTurnCalls[0].options?.model).toBe('gpt-5-codex')
  })

  it('surfaces missing-session through the existing reset hint', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    codex.setEmitSessionReady(false)
    codex.setResult({
      ok: false,
      error: { kind: 'missing-session', message: 'Codex Thread was deleted', diagnostics: [] },
      diagnostics: [],
    })
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    const result = await executor.execute(
      buildAgentJobWork({
        initialTurnId: 'turn-1',
        with: { prompt: 'stale binding', runtime: 'codex', executionSource: 'non-slack' },
      }),
      new AbortController().signal,
    )

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('runtime-session-missing')
    const output = result.output as Record<string, unknown>
    expect(output.kind).toBe('codex')
    expect(output.hint).toBe('reset')
  })

  it('fails with runtime-unavailable instead of silently falling back when Codex is not ready', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    codex.setReady(false)
    codex.setDiagnostic({ severity: 'error', code: 'codex-not-ready', message: 'app-server restarting' })
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    const result = await executor.execute(
      buildAgentJobWork({ with: { prompt: 'codex down', runtime: 'codex', executionSource: 'non-slack' } }),
      new AbortController().signal,
    )

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('runtime-unavailable')
    expect(result.message).toContain('app-server restarting')
    expect((result.output as Record<string, unknown>).kind).toBe('codex')
    expect(codex.runTurnCalls).toHaveLength(0)
  })
})

describe('Codex event projection into the AgentSession channels', () => {
  it('forwards item, tool/activity, usage, and diagnostic events under the Mohist turn id', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    codex.setEvents([
      {
        type: 'message.delta',
        runtimeSessionId: THREAD_ID,
        workDir: WORK_DIR,
        turnId: 'codex-volatile-turn',
        payload: { text: 'hello' },
      },
      {
        type: 'reasoning.delta',
        runtimeSessionId: THREAD_ID,
        workDir: WORK_DIR,
        turnId: 'codex-volatile-turn',
        payload: { summary: 'thinking' },
      },
      {
        type: 'tool_call.started',
        runtimeSessionId: THREAD_ID,
        workDir: WORK_DIR,
        turnId: 'codex-volatile-turn',
        payload: { toolName: 'command', command: 'ls' },
      },
      {
        type: 'tool_call.completed',
        runtimeSessionId: THREAD_ID,
        workDir: WORK_DIR,
        turnId: 'codex-volatile-turn',
        payload: { toolName: 'command', status: 'completed' },
      },
      {
        type: 'tool_call.completed',
        runtimeSessionId: THREAD_ID,
        workDir: WORK_DIR,
        turnId: 'codex-volatile-turn',
        payload: {
          toolCallId: 'file-change:src/index.ts',
          toolName: 'file_change',
          status: 'completed',
          changedFiles: [{ path: 'src/index.ts', operation: 'modified' }],
        },
      },
      {
        type: 'usage.updated',
        runtimeSessionId: THREAD_ID,
        workDir: WORK_DIR,
        turnId: 'codex-volatile-turn',
        payload: { inputTokens: 10, outputTokens: 5, totalTokens: 15 },
      },
      {
        type: 'compaction',
        runtimeSessionId: THREAD_ID,
        workDir: WORK_DIR,
        turnId: 'codex-volatile-turn',
        payload: {},
      },
    ])
    codex.setDiagnostics([
      { severity: 'warning', code: 'server-request-denied', message: 'denied Codex approval request' },
      { severity: 'info', code: 'thread-status', message: 'Codex thread status=idle' },
    ])
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    const result = await executor.execute(
      buildAgentJobWork({
        initialInputId: 'input-1',
        initialTurnId: 'turn-1',
        with: { prompt: 'project events', runtime: 'codex', executionSource: 'non-slack' },
      }),
      new AbortController().signal,
    )

    expect(result.status).toBe('completed')
    const projected = connection.eventCalls.flatMap((call) => turnEvents(call.body))
    const types = projected.map((event) => event.type)
    for (const expected of [
      'message.delta',
      'reasoning.delta',
      'tool_call.started',
      'tool_call.completed',
      'usage.updated',
      'compaction',
      'session.activity',
    ]) {
      expect(types).toContain(expected)
    }
    // The volatile Codex Turn ID never reaches the transcript; the Mohist
    // turn id labels every projected event.
    for (const event of projected) {
      if ('turnId' in event.payload) expect(event.payload.turnId).toBe('turn-1')
    }
    const diagnosticCodes = projected
      .filter((event) => event.type === 'session.activity')
      .map((event) => event.payload.code)
    expect(diagnosticCodes).toContain('server-request-denied')
    expect(diagnosticCodes).toContain('thread-status')
    expect(JSON.stringify(connection.eventCalls)).not.toContain('codex-volatile-turn')
  })

  it('redacts Codex event and diagnostic payloads when manager execution is active', async () => {
    const order: string[] = []
    const connection = makeFakeConnection(order)
    const codex = makeFakeCodexRuntime(order)
    const secret = 'manager-secret-value'
    codex.setEvents([
      {
        type: 'message.delta',
        runtimeSessionId: THREAD_ID,
        workDir: WORK_DIR,
        turnId: 'codex-volatile-turn',
        payload: { text: `leak ${secret}` },
      },
    ])
    codex.setDiagnostics([{ severity: 'warning', code: 'codex-warning', message: `diagnostic ${secret}` }])
    codex.setResult({
      ok: true,
      value: {
        facts: { finalAssistantText: `final ${secret}`, runtimeSessionId: THREAD_ID, workDir: WORK_DIR },
        diagnostics: [],
      },
      diagnostics: [],
    })
    const redactValue = (value: unknown): unknown => {
      if (typeof value === 'string') return value.split(secret).join('***')
      if (Array.isArray(value)) return value.map((item) => redactValue(item))
      if (value && typeof value === 'object') {
        return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, redactValue(item)]))
      }
      return value
    }
    const boundary = {
      hasExpired: () => false,
      mask: (value: string) => value.split(secret).join('***'),
      redact: redactValue,
    } as unknown as ManagerExecutionBoundary
    const executor = makeExecutor(connection.connection, { openCode: null, pi: null, codex: codex.runtime })

    const result = await executor.execute(
      buildAgentJobWork({
        initialInputId: 'input-1',
        initialTurnId: 'turn-1',
        with: { prompt: 'redact events', runtime: 'codex', executionSource: 'non-slack' },
      }),
      new AbortController().signal,
      boundary,
    )

    expect(result.status).toBe('completed')
    const projected = connection.eventCalls.flatMap((call) => turnEvents(call.body))
    expect(projected.find((event) => event.type === 'message.delta')?.payload.text).toBe('leak ***')
    expect(projected.find((event) => event.type === 'session.activity')?.payload.message).toBe('diagnostic ***')
    expect((result.output as Record<string, unknown>).text).toBe('final ***')
    expect(JSON.stringify(result)).not.toContain(secret)
  })
})

describe('projectCodexTurnToWorkItemResult', () => {
  it('labels a successful terminal result as codex and carries the Thread id', () => {
    const result = projectCodexTurnToWorkItemResult(
      {
        ok: true,
        value: {
          facts: { finalAssistantText: 'done', runtimeSessionId: THREAD_ID, workDir: WORK_DIR },
          diagnostics: [],
        },
        diagnostics: [],
      },
      'gpt-5-codex',
      null,
    )

    expect(result.status).toBe('completed')
    expect(result.exitCode).toBe(0)
    expect(result.output).toMatchObject({
      kind: 'codex',
      status: 'success',
      runtimeSessionId: THREAD_ID,
      model: 'gpt-5-codex',
      text: 'done',
    })
  })

  it('normalizes a Codex error kind through mapRuntimeErrorKind and keeps the reset hint', () => {
    const result = projectCodexTurnToWorkItemResult(
      {
        ok: false,
        error: { kind: 'missing-session', message: 'gone', diagnostics: [] },
        diagnostics: [],
      },
      null,
      null,
    )

    expect(result.status).toBe('failed')
    expect(result.exitCode).toBe(1)
    expect(result.error).toEqual({ code: 'runtime-session-missing', message: 'gone' })
    expect(result.output).toMatchObject({ kind: 'codex', status: 'failure', hint: 'reset' })
  })

  it('maps a deadline to the existing timeout kind without a reset hint', () => {
    const result = projectCodexTurnToWorkItemResult(
      {
        ok: false,
        error: { kind: 'deadline-exceeded', message: 'too slow', diagnostics: [] },
        diagnostics: [],
      },
      null,
      null,
    )

    expect(result.error?.code).toBe('timeout')
    expect(result.output).toMatchObject({ kind: 'codex' })
    expect(result.output).not.toMatchObject({ hint: 'reset' })
  })
})
