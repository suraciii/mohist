import { describe, expect, it, vi } from 'vitest'
import { AgentJobExecutor } from '../src/runtime/agent-job-executor.js'
import type { AgentJobRuntimeAccessors } from '../src/runtime/agent-job-executor.js'
import type { ServerConnection } from '../src/server/connection.js'
import type { DispatchWorkItem, JsonObject } from '../src/core/types.js'
import type {
  OpenCodeRuntime,
  RuntimeResult,
  RuntimeTurnEvent,
  RuntimeTurnObserver,
  RuntimeTurnRequest,
  RuntimeTurnResult,
} from '../src/runtime/opencode/index.js'
import type {
  PiRuntime,
  PiResult,
  PiRuntimeEvent,
  PiTurnFacts,
  PiTurnObserver,
  PiTurnRequest,
  PiTurnResult,
} from '../src/runtime/pi/index.js'

interface FakeOpenCodeRuntimeHandles {
  runtime: OpenCodeRuntime
  runTurnCalls: RuntimeTurnRequest[]
  setTurnResult: (result: RuntimeResult<RuntimeTurnResult>) => void
  setTurnEvents: (events: RuntimeTurnEvent[]) => void
}

function makeFakeOpenCodeRuntime(): FakeOpenCodeRuntimeHandles {
  const runTurnCalls: RuntimeTurnRequest[] = []
  let nextResult: RuntimeResult<RuntimeTurnResult> = {
    ok: true,
    value: {
      facts: {
        finalAssistantText: 'agent finished',
        runtimeSessionId: 'ses_default',
        workDir: '/tmp/ws',
      },
      diagnostics: [],
    },
    diagnostics: [],
  }
  let nextEvents: RuntimeTurnEvent[] = []
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => true,
    diagnostic: () => null,
    async runTurn(
      request: RuntimeTurnRequest,
      _signal: AbortSignal,
      observer?: RuntimeTurnObserver,
    ): Promise<RuntimeResult<RuntimeTurnResult>> {
      runTurnCalls.push(request)
      const session = nextResult.ok ? nextResult.value.facts : { runtimeSessionId: 'ses_default', workDir: '/tmp/ws' }
      try {
        await observer?.onSessionReady?.(session)
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error)
        return {
          ok: false,
          error: {
            kind: 'turn-failed',
            message,
            diagnostics: [{ severity: 'error', code: 'turn-failed', message }],
          },
          diagnostics: [{ severity: 'error', code: 'turn-failed', message }],
        }
      }
      if (nextResult.ok) for (const event of nextEvents) observer?.onEvent?.(event)
      return nextResult
    },
  }
  return {
    runtime: runtime as OpenCodeRuntime,
    runTurnCalls,
    setTurnResult(result) {
      nextResult = result
    },
    setTurnEvents(events) {
      nextEvents = events
    },
  }
}

interface FakePiRuntimeHandles {
  runtime: PiRuntime
  runTurnCalls: PiTurnRequest[]
  createSessionCalls: { workDir: string }[]
  setReady: (ready: boolean) => void
  setTurnResult: (result: PiResult<PiTurnResult>) => void
  setCreateSessionResult: (result: PiResult<{ runtimeSessionId: string; workDir: string }>) => void
  setNextSessionId: (sessionId: string) => void
}

function makeFakePiRuntime(): FakePiRuntimeHandles {
  const runTurnCalls: PiTurnRequest[] = []
  const createSessionCalls: { workDir: string }[] = []
  let ready = true
  let nextSessionId = '/virtual/sessions/agent.jsonl'
  let nextResult: PiResult<PiTurnResult> = {
    ok: true,
    value: {
      facts: {
        finalAssistantText: 'pi agent finished',
        runtimeSessionId: nextSessionId,
        workDir: '/tmp/agent-job-ws',
      } satisfies PiTurnFacts,
      diagnostics: [],
    },
    diagnostics: [],
  }
  let nextCreateSession: PiResult<{ runtimeSessionId: string; workDir: string }> = {
    ok: true,
    value: { runtimeSessionId: nextSessionId, workDir: '/tmp/agent-job-ws' },
    diagnostics: [],
  }
  const runtime: Partial<PiRuntime> = {
    ready: () => ready,
    diagnostic: () => null,
    catalog: () => ({ models: [{ provider: 'openai', id: 'gpt-5.5', thinkingLevels: ['low', 'high'] }] }),
    async createSession(request: {
      target: { workDir: string }
    }): Promise<PiResult<{ runtimeSessionId: string; workDir: string }>> {
      createSessionCalls.push({ workDir: request.target.workDir })
      return nextCreateSession
    },
    async runTurn(
      request: PiTurnRequest,
      _signal: AbortSignal,
      observer?: PiTurnObserver,
    ): Promise<PiResult<PiTurnResult>> {
      runTurnCalls.push(request)
      if (observer?.onEvent) {
        const sample: PiRuntimeEvent[] = [
          {
            id: 'pi-msg-1',
            type: 'message',
            runtimeSessionId: request.target.runtimeSessionId ?? nextSessionId,
            workDir: request.target.workDir,
            payload: { role: 'assistant', content: 'thinking' },
          },
          {
            id: 'pi-msg-2',
            type: 'message',
            runtimeSessionId: request.target.runtimeSessionId ?? nextSessionId,
            workDir: request.target.workDir,
            payload: { role: 'assistant', content: 'done' },
          },
        ]
        for (const event of sample) observer.onEvent(event)
      }
      return nextResult
    },
  }
  return {
    runtime: runtime as PiRuntime,
    runTurnCalls,
    createSessionCalls,
    setReady(value) {
      ready = value
    },
    setTurnResult(result) {
      nextResult = result
    },
    setCreateSessionResult(result) {
      nextCreateSession = result
    },
    setNextSessionId(value) {
      nextSessionId = value
      if (nextResult.ok) {
        nextResult = {
          ok: true,
          value: {
            facts: { ...nextResult.value.facts, runtimeSessionId: value },
            diagnostics: [],
          },
          diagnostics: [],
        }
      }
      nextCreateSession = {
        ok: true,
        value: { runtimeSessionId: value, workDir: '/tmp/agent-job-ws' },
        diagnostics: [],
      }
    },
  }
}

function makeAccessors(
  overrides: Partial<{ openCode: OpenCodeRuntime | null; pi: PiRuntime | null }> = {},
): AgentJobRuntimeAccessors {
  return {
    openCode: overrides.openCode === undefined ? makeFakeOpenCodeRuntime().runtime : overrides.openCode,
    pi: overrides.pi === undefined ? null : overrides.pi,
  }
}

function makeAccessorsFromFake(
  handles: FakeOpenCodeRuntimeHandles | FakePiRuntimeHandles | null,
  runtimeName: 'opencode' | 'pi',
): AgentJobRuntimeAccessors {
  return {
    openCode: runtimeName === 'opencode' && handles ? (handles as FakeOpenCodeRuntimeHandles).runtime : null,
    pi: runtimeName === 'pi' && handles ? (handles as FakePiRuntimeHandles).runtime : null,
  }
}

interface FakeConnectionHandles {
  connection: ServerConnection
  attachCalls: Array<{
    projectId: string
    sessionId: string
    body: Record<string, unknown>
  }>
  eventCalls: Array<{ projectId: string; sessionId: string; body: Record<string, unknown> }>
  setAgentSession: (session: { runtimeSessionId: string | null } | null) => void
  setEventWriter: (writer: (body: Record<string, unknown>) => Promise<void>) => void
}

function makeFakeConnection(): FakeConnectionHandles {
  const attachCalls: FakeConnectionHandles['attachCalls'] = []
  const eventCalls: FakeConnectionHandles['eventCalls'] = []
  let agentSession: { runtimeSessionId: string | null } | null = null
  let eventWriter: (body: Record<string, unknown>) => Promise<void> = async () => {}
  const connection = {
    async openAgentSession() {},
    async attachAgentSession(
      projectId: string,
      sessionId: string,
      body: Record<string, unknown>,
      _signal: AbortSignal,
    ) {
      attachCalls.push({ projectId, sessionId, body })
    },
    async getAgentSession(_projectId: string, sessionId: string, _signal: AbortSignal) {
      if (agentSession === null) return null
      return {
        runtimeSessionId: agentSession.runtimeSessionId,
        workDir: '/tmp/ws',
      } as never
    },
    async agentSessionRuntimeEvents(projectId: string, sessionId: string, body: Record<string, unknown>) {
      eventCalls.push({ projectId, sessionId, body })
      await eventWriter(body)
    },
  } as unknown as ServerConnection
  return {
    connection,
    attachCalls,
    eventCalls,
    setAgentSession(session) {
      agentSession = session
    },
    setEventWriter(writer) {
      eventWriter = writer
    },
  }
}

function buildAgentJobWork(overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: '',
    workId: 'aj-1',
    workType: 'task',
    ownerKind: 'agent-job',
    agentJobId: 'aj-1',
    agentSessionId: 'session-1',
    projectId: 'proj-1',
    with: { prompt: 'do the agent thing' },
    variables: {
      workspace: { path: '/tmp/agent-job-ws', branch: null, changeDir: null },
    },
    ...overrides,
  }
}

describe('AgentJobExecutor selects the runtime from the dispatch', () => {
  it('selects OpenCodeRuntime for a dispatch with runtime: opencode', async () => {
    const openCode = makeFakeOpenCodeRuntime()
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, {
      openCode: openCode.runtime,
      pi: pi.runtime,
    })

    const work = buildAgentJobWork({
      with: { prompt: 'ship it', runtime: 'opencode' },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(openCode.runTurnCalls).toHaveLength(1)
    expect(pi.runTurnCalls).toHaveLength(0)
    const parsed = result.output as Record<string, unknown>
    expect(parsed.kind).toBe('opencode')
  })

  it('passes the same complete startup-before-task prompt to OpenCode and Pi', async () => {
    const openCode = makeFakeOpenCodeRuntime()
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, {
      openCode: openCode.runtime,
      pi: pi.runtime,
    })
    const startup = {
      projectId: 'proj_1',
      sessionId: 'sess_child',
      parentSessionId: 'sess_parent',
      allowedSubagents: [
        {
          agentId: 'agent_allowed',
          nameAtLaunch: 'allowed-agent',
          descriptionAtLaunch: 'stable launch description',
        },
      ],
      spawnCommand: 'mo agent spawn <agent-ref> --parent-session <session-id>',
      workDir: '/inherited/agent-workspace',
      pinnedRunnerId: 'runner_pinned',
      agentId: 'agent_target',
      agentName: 'target-agent',
    } as const

    const commonWork = {
      agentSessionId: null,
      agentSessionStartup: startup,
      with: { prompt: 'target task', instructions: 'follow the brief', runtime: 'opencode' },
    } satisfies Partial<DispatchWorkItem>
    const openCodeResult = await executor.execute(buildAgentJobWork(commonWork), new AbortController().signal)
    const piResult = await executor.execute(
      buildAgentJobWork({
        ...commonWork,
        workId: 'aj-pi',
        agentJobId: 'aj-pi',
        with: { ...commonWork.with, runtime: 'pi' },
      }),
      new AbortController().signal,
    )

    expect(openCodeResult.status).toBe('completed')
    expect(piResult.status).toBe('completed')
    const openCodePrompt = openCode.runTurnCalls[0]?.prompt
    const piPrompt = pi.runTurnCalls[0]?.prompt
    expect(openCodePrompt).toBeDefined()
    expect(piPrompt).toBe(openCodePrompt)
    expect(openCodePrompt).toMatch(
      /^\[mohist-agent-session-startup\][\s\S]*\[\/mohist-agent-session-startup\]\n\n[\s\S]*target task$/,
    )
    for (const value of [
      'agent_target',
      'target-agent',
      '/inherited/agent-workspace',
      'runner_pinned',
      'sess_parent',
      'sess_child',
      'agent_allowed',
      'allowed-agent',
      'stable launch description',
      'mo agent spawn <agent-ref> --parent-session <session-id>',
    ]) {
      expect(openCodePrompt).toContain(value)
    }
  })

  it('selects PiRuntime for a dispatch with runtime: pi', async () => {
    const openCode = makeFakeOpenCodeRuntime()
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, {
      openCode: openCode.runtime,
      pi: pi.runtime,
    })

    const work = buildAgentJobWork({
      with: { prompt: 'ship it on pi', runtime: 'pi' },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(pi.runTurnCalls).toHaveLength(1)
    expect(openCode.runTurnCalls).toHaveLength(0)
    const parsed = result.output as Record<string, unknown>
    expect(parsed.kind).toBe('pi')
  })

  it('treats an absent dispatch `runtime` as opencode (legacy partial rollout)', async () => {
    const openCode = makeFakeOpenCodeRuntime()
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, {
      openCode: openCode.runtime,
      pi: pi.runtime,
    })

    const work = buildAgentJobWork({ with: { prompt: 'legacy dispatch' } })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(openCode.runTurnCalls).toHaveLength(1)
    expect(pi.runTurnCalls).toHaveLength(0)
  })

  it('fails with runtime-unavailable when the selected pi runtime is not ready', async () => {
    const openCode = makeFakeOpenCodeRuntime()
    const pi = makeFakePiRuntime()
    pi.setReady(false)
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, {
      openCode: openCode.runtime,
      pi: pi.runtime,
    })

    const work = buildAgentJobWork({
      with: { prompt: 'pi unavailable', runtime: 'pi' },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('runtime-unavailable')
    expect(result.message).toMatch(/Pi runtime/)
    // Critical: NO silent fallback to OpenCode.
    expect(openCode.runTurnCalls).toHaveLength(0)
    expect(pi.runTurnCalls).toHaveLength(0)
  })

  it('fails with runtime-unavailable when the selected opencode runtime is not ready', async () => {
    const openCode = makeFakeOpenCodeRuntime()
    openCode.runtime.ready = () => false
    openCode.runtime.diagnostic = () => ({ severity: 'warning', code: 'opencode-not-ready', message: 'opencode down' })
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, {
      openCode: openCode.runtime,
      pi: pi.runtime,
    })

    const work = buildAgentJobWork({
      with: { prompt: 'opencode unavailable', runtime: 'opencode' },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('runtime-unavailable')
    expect(result.message).toMatch(/OpenCode runtime/)
    // Critical: NO silent fallback to Pi.
    expect(pi.runTurnCalls).toHaveLength(0)
  })

  it('fails when the selected pi runtime accessor returns null (host wiring)', async () => {
    const openCode = makeFakeOpenCodeRuntime()
    const connection = makeFakeConnection()
    // Late-binding accessor returns null (e.g. Pi runtime not constructed yet).
    const executor = new AgentJobExecutor(connection.connection, {
      openCode: openCode.runtime,
      pi: () => null,
    })

    const work = buildAgentJobWork({
      with: { prompt: 'pi accessor null', runtime: 'pi' },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('runtime-unavailable')
    // No silent fallback to OpenCode.
    expect(openCode.runTurnCalls).toHaveLength(0)
  })

  it('does not flag `runtime` as an unknown dispatch option key (pi path)', async () => {
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const work = buildAgentJobWork({
      with: { prompt: 'audit pi', runtime: 'pi' },
    })
    await executor.execute(work, new AbortController().signal)

    expect(pi.runTurnCalls).toHaveLength(1)
    expect(pi.runTurnCalls[0].options?.unknownKeys ?? []).toEqual([])
  })
})

describe('AgentJobExecutor drives PiRuntime end-to-end', () => {
  it('creates a Pi session when no binding exists and executes the turn', async () => {
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    connection.setAgentSession({ runtimeSessionId: null })
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const work = buildAgentJobWork({
      with: { prompt: 'execute on pi', runtime: 'pi', model: 'openai/gpt-5.5', reasoningEffort: 'high' },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(pi.createSessionCalls).toHaveLength(1)
    expect(pi.runTurnCalls).toHaveLength(1)
    const request = pi.runTurnCalls[0]
    expect(request.target.runtime).toBe('pi')
    expect(request.target.workDir).toBe('/tmp/agent-job-ws')
    expect(request.options?.model).toBe('openai/gpt-5.5')
    expect(request.options?.variant).toBeNull()
    expect(request.options?.reasoningEffort).toBe('high')
  })

  it('rejects a Pi runtime-specific variant instead of treating it as reasoning effort', async () => {
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const result = await executor.execute(
      buildAgentJobWork({
        with: { prompt: 'do not alias', runtime: 'pi', variant: 'high' },
      }),
      new AbortController().signal,
    )

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('incompatible-execution-configuration')
    expect(pi.runTurnCalls).toHaveLength(0)
  })

  it('rejects reasoning effort without an explicit model before opening a Pi session', async () => {
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const result = await executor.execute(
      buildAgentJobWork({
        with: { prompt: 'needs model', runtime: 'pi', reasoningEffort: 'high' },
      }),
      new AbortController().signal,
    )

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('incompatible-execution-configuration')
    expect(pi.createSessionCalls).toHaveLength(0)
    expect(pi.runTurnCalls).toHaveLength(0)
  })

  it('labels Pi session-creation failures with the Pi runtime', async () => {
    const pi = makeFakePiRuntime()
    pi.setCreateSessionResult({
      ok: false,
      error: {
        kind: 'turn-failed',
        message: 'Pi session creation failed',
        diagnostics: [
          {
            severity: 'error',
            code: 'session-create-failed',
            message: 'provider credentials unavailable',
          },
        ],
      },
      diagnostics: [
        {
          severity: 'error',
          code: 'session-create-failed',
          message: 'provider credentials unavailable',
        },
      ],
    })
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const result = await executor.execute(
      buildAgentJobWork({ with: { prompt: 'create a Pi session', runtime: 'pi' } }),
      new AbortController().signal,
    )

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('turn-failed')
    expect((result.output as Record<string, unknown>).kind).toBe('pi')
  })

  it('reuses an existing pi binding on a follow-up dispatch', async () => {
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    connection.setAgentSession({ runtimeSessionId: '/virtual/sessions/existing.jsonl' })
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const work = buildAgentJobWork({
      with: { prompt: 'follow-up', runtime: 'pi' },
    })
    await executor.execute(work, new AbortController().signal)

    expect(pi.createSessionCalls).toHaveLength(0)
    expect(pi.runTurnCalls).toHaveLength(1)
    expect(pi.runTurnCalls[0].target.runtimeSessionId).toBe('/virtual/sessions/existing.jsonl')
  })

  it('labels the terminal output with the runtime that executed', async () => {
    const pi = makeFakePiRuntime()
    pi.setNextSessionId('/virtual/sessions/labeled.jsonl')
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const work = buildAgentJobWork({
      with: { prompt: 'label me', runtime: 'pi', model: 'openai/gpt-5.5' },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    const parsed = result.output as JsonObject as Record<string, unknown>
    expect(parsed.kind).toBe('pi')
    expect(parsed.status).toBe('success')
    expect(parsed.runtimeSessionId).toBe('/virtual/sessions/labeled.jsonl')
    expect(parsed.model).toBe('openai/gpt-5.5')
  })

  it('projects Pi turn facts through the existing AgentSession observer channel', async () => {
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const work = buildAgentJobWork({
      agentSessionId: 'session-pi',
      with: { prompt: 'project pi facts', runtime: 'pi' },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    // Attach + session.input + at least one runtime event forwarded.
    expect(connection.attachCalls).toHaveLength(1)
    expect(connection.attachCalls[0].body).toMatchObject({
      runtimeSessionId: expect.stringMatching(/\.jsonl$/) as unknown as string,
      workDir: '/tmp/agent-job-ws',
      workId: 'aj-1',
      agentJobId: 'aj-1',
    })
    const eventTypes = connection.eventCalls.map(
      (call) => (call.body.runtimeEvents as Array<{ type: string }>)[0]?.type,
    )
    expect(eventTypes).toContain('session.input')
    // Pi-runtime events (from the fake's observer sample) reach the server.
    expect(eventTypes.filter((t) => t === 'message').length).toBeGreaterThan(0)
  })

  it('forwards uncredentialed-model failures as actionable turn-failed without swallowing them', async () => {
    const pi = makeFakePiRuntime()
    pi.setTurnResult({
      ok: false,
      error: {
        kind: 'turn-failed',
        message: 'Pi rejected the selected model',
        diagnostics: [
          {
            severity: 'error',
            code: 'model-rejected',
            message: 'provider not configured for model',
          },
        ],
      },
      diagnostics: [
        {
          severity: 'error',
          code: 'model-rejected',
          message: 'provider not configured for model',
        },
      ],
    })
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const work = buildAgentJobWork({
      with: { prompt: 'no creds', runtime: 'pi', model: 'openai/gpt-uncredentialed' },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('turn-failed')
    expect(result.error?.message).toMatch(/Pi rejected the selected model/)
    // Actionable diagnostic surfaces (the runtime is the final validator).
    const parsed = result.output as Record<string, unknown>
    const diagnostics = parsed.diagnostics as Array<{ code: string; message: string }>
    expect(diagnostics).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ code: 'model-rejected', message: expect.stringMatching(/provider/) }),
      ]),
    )
    // The terminal output keeps the runtime label so callers know which backend rejected the model.
    expect(parsed.kind).toBe('pi')
  })

  it('surfaces missing-session with the reset hint', async () => {
    const pi = makeFakePiRuntime()
    pi.setTurnResult({
      ok: false,
      error: {
        kind: 'missing-session',
        message: 'Pi session file is missing',
        diagnostics: [],
      },
      diagnostics: [],
    })
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const work = buildAgentJobWork({
      with: { prompt: 'stale binding', runtime: 'pi' },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('runtime-session-missing')
    const parsed = result.output as Record<string, unknown>
    expect(parsed.kind).toBe('pi')
    expect(parsed.hint).toBe('reset')
  })

  it('passes a fresh prompt from the dispatch through the composed prompt helper', async () => {
    const pi = makeFakePiRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessorsFromFake(pi, 'pi'))

    const work = buildAgentJobWork({
      with: { prompt: 'main task', instructions: 'be terse', runtime: 'pi' },
    })
    await executor.execute(work, new AbortController().signal)

    expect(pi.runTurnCalls).toHaveLength(1)
    expect(pi.runTurnCalls[0].prompt).toBe('be terse\n\nmain task')
  })
})
