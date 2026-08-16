import { describe, expect, it as vitestIt, vi } from 'vitest'
import { AgentJobExecutor, projectTurnToWorkItemResult } from '../src/runtime/agent-job-executor.js'
import type { AgentJobRuntimeAccessors } from '../src/runtime/agent-job-executor.js'
import type { ServerConnection } from '../src/server/connection.js'
import type { DispatchWorkItem } from '../src/core/types.js'
import type {
  OpenCodeRuntime,
  RuntimeResult,
  RuntimeTurnFacts,
  RuntimeTurnEvent,
  RuntimeTurnObserver,
  RuntimeTurnRequest,
  RuntimeTurnResult,
} from '../src/runtime/opencode/index.js'
import { capturedLogs } from './support/logger-test.js'
import {
  createDefaultRunnerTestResources,
  withDefaultRunnerTestResources,
  withTestRunnerResources,
} from './support/test-resources.js'

function it(name: string, body: () => Promise<void> | void): void {
  vitestIt(name, async () => {
    await withDefaultRunnerTestResources(async () => await body())
  })
}

interface FakeRuntimeHandles {
  runtime: OpenCodeRuntime
  runTurnCalls: RuntimeTurnRequest[]
  setTurnResult: (result: RuntimeResult<RuntimeTurnResult>) => void
  setTurnEvents: (events: RuntimeTurnEvent[]) => void
}

function makeFakeRuntime(): FakeRuntimeHandles {
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

function makeAccessors(runtime: OpenCodeRuntime | null = makeFakeRuntime().runtime): AgentJobRuntimeAccessors {
  return {
    openCode: runtime,
    pi: null,
  }
}

interface FakeConnectionHandles {
  connection: ServerConnection
  openCalls: Array<{
    projectId: string
    sessionId: string
    body: Record<string, unknown>
  }>
  attachCalls: Array<{
    projectId: string
    sessionId: string
    body: Record<string, unknown>
  }>
  eventCalls: Array<{ projectId: string; sessionId: string; body: Record<string, unknown> }>
  bindingCalls: string[]
  setAgentSession: (session: { runtimeSessionId: string | null } | null) => void
  setEventWriter: (writer: (body: Record<string, unknown>) => Promise<void>) => void
}

function makeFakeConnection(): FakeConnectionHandles {
  const openCalls: FakeConnectionHandles['openCalls'] = []
  const attachCalls: FakeConnectionHandles['attachCalls'] = []
  const eventCalls: FakeConnectionHandles['eventCalls'] = []
  const bindingCalls: string[] = []
  let agentSession: { runtimeSessionId: string | null } | null = null
  let eventWriter: (body: Record<string, unknown>) => Promise<void> = async () => {}
  const connection = {
    async openAgentSession(projectId: string, sessionId: string, body: Record<string, unknown>, _signal: AbortSignal) {
      bindingCalls.push('open')
      openCalls.push({ projectId, sessionId, body })
    },
    async attachAgentSession(
      projectId: string,
      sessionId: string,
      body: Record<string, unknown>,
      _signal: AbortSignal,
    ) {
      bindingCalls.push('attach')
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
    openCalls,
    attachCalls,
    eventCalls,
    bindingCalls,
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

describe('AgentJobExecutor drives OpenCodeRuntime directly', () => {
  it('calls OpenCodeRuntime.runTurn with a flat Agent-owned request', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({
      with: {
        prompt: 'review the diff',
        instructions: 'be terse',
        model: 'openai/gpt-5.5',
        variant: 'high',
      },
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(runtime.runTurnCalls).toHaveLength(1)
    const request = runtime.runTurnCalls[0]
    expect(request.target.runtime).toBe('opencode')
    expect(request.target.workDir).toBe('/tmp/agent-job-ws')
    expect(request.target.runtimeSessionId).toBeNull()
    expect(request.options?.model).toEqual({ providerID: 'openai', modelID: 'gpt-5.5' })
    expect(request.options?.variant).toBe('high')
    expect(request.prompt).toBe('be terse\n\nreview the diff')
  })

  it('returns the legacy {kind, status, runtimeSessionId, model, variant, text, error} envelope', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({
      with: {
        prompt: 'do the thing',
        model: 'anthropic/claude-sonnet-4',
        variant: 'max',
      },
    })
    runtime.setTurnResult({
      ok: true,
      value: {
        facts: {
          finalAssistantText: 'done',
          runtimeSessionId: 'ses_xyz',
          workDir: '/tmp/agent-job-ws',
        },
        diagnostics: [],
      },
      diagnostics: [],
    })

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    const parsed = result.output as Record<string, unknown>
    expect(parsed.kind).toBe('opencode')
    expect(parsed.status).toBe('success')
    expect(parsed.runtimeSessionId).toBe('ses_xyz')
    expect(parsed.model).toBe('anthropic/claude-sonnet-4')
    expect(parsed.variant).toBe('max')
    expect(parsed.text).toBe('done')
    expect(parsed.error).toBeNull()
  })

  it('never resolves a Workflow Action for an AgentJob dispatch', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    // Even if `with.uses` was stamped (it should never be in the
    // new server-side envelope), the executor does not consult an
    // Action registry.
    const work = buildAgentJobWork({
      uses: 'mohist/opencode',
      with: {
        prompt: 'no action resolution',
        uses: 'mohist/opencode',
      },
    })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe('completed')
    expect(runtime.runTurnCalls).toHaveLength(1)
  })

  it('rejects a non-agent-job dispatch with a clear failure', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({ ownerKind: 'workflow', agentJobId: null })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe('failed')
    expect(result.message).toMatch(/non-agent-job/)
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it('requires the OpenCode runtime to be present', async () => {
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(null))
    const work = buildAgentJobWork()
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe('failed')
    expect(result.message).toMatch(/requires the OpenCode runtime/)
  })

  it('fails when the runtime is not yet ready', async () => {
    const connection = makeFakeConnection()
    const runtime: Partial<OpenCodeRuntime> = {
      ready: () => false,
      diagnostic: () => ({ severity: 'warning', code: 'runtime-not-ready', message: 'not ready' }),
      async runTurn() {
        throw new Error('should not be called')
      },
    }
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime as OpenCodeRuntime))
    const work = buildAgentJobWork()
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe('failed')
    expect(result.message).toMatch(/ready/)
  })
})

describe('AgentJobExecutor reports the runtime session binding', () => {
  it('reports the runtime session id back via attachAgentSession after a successful turn', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    connection.setAgentSession({ runtimeSessionId: null })
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    runtime.setTurnResult({
      ok: true,
      value: {
        facts: {
          finalAssistantText: 'ran',
          runtimeSessionId: 'ses_bound',
          workDir: '/tmp/agent-job-ws',
        },
        diagnostics: [],
      },
      diagnostics: [],
    })

    const work = buildAgentJobWork({
      agentSessionId: 'session-bound',
      with: { prompt: 'report me' },
    })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe('completed')
    expect(connection.openCalls).toEqual([
      {
        projectId: 'proj-1',
        sessionId: 'session-bound',
        body: { workDir: '/tmp/agent-job-ws' },
      },
    ])
    expect(connection.attachCalls).toHaveLength(1)
    expect(connection.bindingCalls).toEqual(['open', 'attach'])
    const attach = connection.attachCalls[0]
    expect(attach.projectId).toBe('proj-1')
    expect(attach.sessionId).toBe('session-bound')
    expect(attach.body).toMatchObject({
      runtimeSessionId: 'ses_bound',
      workDir: '/tmp/agent-job-ws',
      workId: 'aj-1',
      agentJobId: 'aj-1',
    })
  })

  it('forwards matching runtime events to the canonical AgentSession', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))
    runtime.setTurnEvents([
      {
        type: 'message.delta',
        runtimeSessionId: 'ses_default',
        workDir: '/tmp/ws',
        payload: { text: 'working' },
      },
    ])

    await executor.execute(buildAgentJobWork(), new AbortController().signal)

    expect(connection.attachCalls).toHaveLength(1)
    expect(connection.eventCalls).toHaveLength(2)
    expect(connection.eventCalls.map((call) => (call.body.runtimeEvents as Array<{ type: string }>)[0]?.type)).toEqual([
      'session.input',
      'message.delta',
    ])
    expect(connection.eventCalls[1]).toEqual({
      projectId: 'proj-1',
      sessionId: 'session-1',
      body: {
        workId: 'aj-1',
        workType: 'task',
        stage: undefined,
        runtimeSessionId: 'ses_default',
        runtimeEvents: [{ type: 'message.delta', payload: { text: 'working' } }],
      },
    })
  })

  it('does not let transcript event write failures adjudicate the AgentJob', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))
    runtime.setTurnEvents([
      { type: 'message.delta', runtimeSessionId: 'ses_default', workDir: '/tmp/ws', payload: { text: 'working' } },
    ])
    connection.setEventWriter(async (body) => {
      const type = (body.runtimeEvents as Array<{ type: string }>)[0]?.type
      if (type === 'message.delta') throw new Error('transcript endpoint offline')
    })
    const result = await executor.execute(buildAgentJobWork(), new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(capturedLogs()).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          level: 'ERROR',
          message: 'agent-session runtime event failed',
          fields: expect.objectContaining({
            exception: expect.objectContaining({ message: 'transcript endpoint offline' }),
          }),
        }),
      ]),
    )
  })

  it('writes runtime events in observation order', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))
    let releaseFirst!: () => void
    let markFirstStarted!: () => void
    const firstStarted = new Promise<void>((resolve) => {
      markFirstStarted = resolve
    })
    const firstBlocked = new Promise<void>((resolve) => {
      releaseFirst = resolve
    })
    const started: string[] = []
    connection.setEventWriter(async (body) => {
      const type = (body.runtimeEvents as Array<{ type: string }>)[0]?.type ?? 'unknown'
      started.push(type)
      if (type === 'message.delta') {
        markFirstStarted()
        await firstBlocked
      }
    })
    runtime.setTurnEvents([
      { type: 'message.delta', runtimeSessionId: 'ses_default', workDir: '/tmp/ws', payload: { text: 'one' } },
      { type: 'reasoning.delta', runtimeSessionId: 'ses_default', workDir: '/tmp/ws', payload: { text: 'two' } },
    ])

    const execution = executor.execute(buildAgentJobWork(), new AbortController().signal)
    await firstStarted
    expect(started).toEqual(['session.input', 'message.delta'])
    releaseFirst()
    await execution
    expect(started).toEqual(['session.input', 'message.delta', 'reasoning.delta'])
  })

  it('does not report a binding when the dispatch carries no AgentSessionId', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({ agentSessionId: null })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe('completed')
    expect(connection.attachCalls).toHaveLength(0)
  })

  it('does not create or prompt when the authoritative binding lookup fails', async () => {
    const runtime = makeFakeRuntime()
    const connection = {
      async getAgentSession() {
        throw new Error('session lookup offline')
      },
    } as unknown as ServerConnection
    const executor = new AgentJobExecutor(connection, makeAccessors(runtime.runtime))

    const result = await executor.execute(buildAgentJobWork(), new AbortController().signal)

    expect(result.status).toBe('failed')
    expect(result.message).toContain('session lookup offline')
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it('attaches the runtimeSessionId from an existing binding on a follow-up dispatch', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    connection.setAgentSession({ runtimeSessionId: 'ses_existing' })
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({ agentSessionId: 'session-existing' })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(runtime.runTurnCalls).toHaveLength(1)
    expect(runtime.runTurnCalls[0].target.runtimeSessionId).toBe('ses_existing')
    expect(connection.openCalls).toEqual([
      {
        projectId: 'proj-1',
        sessionId: 'session-existing',
        body: { workDir: '/tmp/ws' },
      },
    ])
    expect(connection.attachCalls).toHaveLength(1)
    expect(connection.bindingCalls).toEqual(['open', 'attach'])
    expect(connection.attachCalls[0].body.runtimeSessionId).toMatch(/ses_/)
  })

  it('fails before prompting when the runtime binding cannot be recorded', async () => {
    const runtime = makeFakeRuntime()
    const connection: ServerConnection = {
      async openAgentSession() {
        return null
      },
      async attachAgentSession() {
        throw new Error('attach endpoint offline')
      },
      async getAgentSession() {
        return { runtimeSessionId: null } as never
      },
    } as unknown as ServerConnection
    const executor = new AgentJobExecutor(connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork()
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe('failed')
    expect(result.message).toBe('attach endpoint offline')
    const diagnostics = (result.output as Record<string, unknown>).diagnostics as Array<{
      code: string
      message: string
    }>
    expect(diagnostics).toEqual(
      expect.arrayContaining([expect.objectContaining({ code: 'turn-failed', message: 'attach endpoint offline' })]),
    )
  })
})

describe('AgentJobExecutor materialises the launch-time snapshot', () => {
  it('does not consult the Agent definition; it reads the dispatch payload only', async () => {
    // Editing/archiving the Agent definition while the job is in
    // flight does not change the running turn's inputs. The
    // executor reads only `work.with`; there is no Agent lookup,
    // and `work.with` is the launch-time snapshot that the server
    // already wrote into the dispatch envelope.
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const launchTimeInstructions = 'be brief; cite line numbers'
    const launchTimeModel = 'openai/gpt-5.5'
    const launchTimeVariant = 'high'
    const work = buildAgentJobWork({
      with: {
        prompt: 'audit the diff',
        instructions: launchTimeInstructions,
        model: launchTimeModel,
        variant: launchTimeVariant,
      },
    })
    await executor.execute(work, new AbortController().signal)

    expect(runtime.runTurnCalls).toHaveLength(1)
    const request = runtime.runTurnCalls[0]
    expect(request.prompt).toBe(`${launchTimeInstructions}\n\naudit the diff`)
    expect(request.options?.model).toEqual({ providerID: 'openai', modelID: 'gpt-5.5' })
    expect(request.options?.variant).toBe(launchTimeVariant)
  })

  it('does not mutate state across calls; each invocation reads a fresh dispatch snapshot', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    // First launch pins one snapshot
    await executor.execute(
      buildAgentJobWork({ with: { prompt: 'first', instructions: 'original' } }),
      new AbortController().signal,
    )
    // A second call with a different dispatch payload must use the new payload
    await executor.execute(
      buildAgentJobWork({ with: { prompt: 'second', instructions: 'updated' } }),
      new AbortController().signal,
    )

    expect(runtime.runTurnCalls).toHaveLength(2)
    expect(runtime.runTurnCalls[0].prompt).toBe('original\n\nfirst')
    expect(runtime.runTurnCalls[1].prompt).toBe('updated\n\nsecond')
  })
})

describe('AgentJobExecutor parses the dispatch payload', () => {
  it('rejects a dispatch without a prompt', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({ with: { instructions: 'no prompt' } })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe('failed')
    expect(result.message).toMatch(/prompt/)
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  it('rejects a malformed model identifier', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({ with: { prompt: 'go', model: 'not a model id' } })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe('failed')
    expect(result.message).toMatch(/model/)
    expect(runtime.runTurnCalls).toHaveLength(0)
  })

  vitestIt.each([
    ['null workspace', null],
    ['non-object workspace', 'invalid'],
    ['missing path', {}],
    ['empty path', { path: '' }],
    ['whitespace path', { path: '   ' }],
    ['non-string path', { path: 42 }],
  ])('rejects %s instead of using the runner default workdir', async (_name, workspace) => {
    await withDefaultRunnerTestResources(async () => {
      const runtime = makeFakeRuntime()
      const connection = makeFakeConnection()
      const executor = new AgentJobExecutor(
        connection.connection,
        makeAccessors(runtime.runtime),
        null,
        '/virtual/runner',
      )

      const work = buildAgentJobWork({ variables: { workspace } })
      const result = await executor.execute(work, new AbortController().signal)
      expect(result.status).toBe('failed')
      expect(result.message).toMatch(/workspace\.path/)
      expect(runtime.runTurnCalls).toHaveLength(0)
    })
  })

  it('uses the runner default workdir when a direct AgentJob has no workspace', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const result = await executor.execute(buildAgentJobWork({ variables: {} }), new AbortController().signal)

    expect(result.status).toBe('completed')
    expect(runtime.runTurnCalls[0]?.target.workDir).toBe(process.cwd())
  })

  it('does not flag `runtime` as an unknown dispatch option key', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork({
      with: { prompt: 'audit', runtime: 'opencode' },
    })
    await executor.execute(work, new AbortController().signal)

    expect(runtime.runTurnCalls).toHaveLength(1)
    expect(runtime.runTurnCalls[0].options?.unknownKeys ?? []).toEqual([])
  })
})

describe('AgentJobExecutor surfaces a missing-session turn as a Reset hint', () => {
  it("returns the legacy {kind, status, ..., hint: 'reset'} envelope on a missing session", async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    runtime.setTurnResult({
      ok: false,
      error: {
        kind: 'missing-session',
        message: 'no physical session',
        diagnostics: [],
      },
      diagnostics: [],
    })

    const work = buildAgentJobWork({ agentSessionId: 'session-orphan' })
    const result = await executor.execute(work, new AbortController().signal)
    expect(result.status).toBe('failed')
    const parsed = result.output as Record<string, unknown>
    expect(parsed.kind).toBe('opencode')
    expect(parsed.status).toBe('failure')
    expect(parsed.runtimeSessionId).toBeNull()
    expect(parsed.error).toMatch(/no physical session/)
    expect(parsed.hint).toBe('reset')
  })
})

describe('AgentJobExecutor work-result projection', () => {
  it('maps a successful RuntimeResult to a completed work result', () => {
    const result: RuntimeResult<RuntimeTurnResult> = {
      ok: true,
      value: {
        facts: {
          finalAssistantText: 'yes',
          runtimeSessionId: 'ses_a',
          workDir: '/tmp/w',
        } satisfies RuntimeTurnFacts,
        diagnostics: [],
      },
      diagnostics: [],
    }
    const workResult = projectTurnToWorkItemResult(result, 'opencode', 'openai/gpt-5.5', 'high')
    expect(workResult.status).toBe('completed')
    expect(workResult.exitCode).toBe(0)
    expect(workResult.error).toBeUndefined()
    const parsed = workResult.output as Record<string, unknown>
    expect(parsed.status).toBe('success')
    expect(parsed.runtimeSessionId).toBe('ses_a')
    expect(parsed.text).toBe('yes')
  })

  it('maps a failed RuntimeResult to a failed work result with the runtime error', () => {
    const result: RuntimeResult<RuntimeTurnResult> = {
      ok: false,
      error: { kind: 'turn-failed', message: 'boom', diagnostics: [] },
      diagnostics: [],
    }
    const workResult = projectTurnToWorkItemResult(result, 'opencode', null, null)
    expect(workResult.status).toBe('failed')
    expect(workResult.error).toEqual({ code: 'turn-failed', message: 'boom' })
    expect(workResult.exitCode).toBe(1)
    const parsed = workResult.output as Record<string, unknown>
    expect(parsed.status).toBe('failure')
    expect(parsed.error).toBe('boom')
  })
})

describe('AgentJobExecutor workflow expectation evaluation', () => {
  const workDir = '/tmp/agent-job-ws'

  function expectationWork(expect: Record<string, unknown>): DispatchWorkItem {
    return buildAgentJobWork({ expect })
  }

  it('evaluates the frozen expect after the turn settles and reports the typed evaluation alongside the agent result', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))
    runtime.setTurnResult({
      ok: true,
      value: {
        facts: {
          finalAssistantText: 'the plan is written\n<promise>done</promise>',
          runtimeSessionId: 'ses_expect',
          workDir,
        },
        diagnostics: [],
      },
      diagnostics: [],
    })

    const work = expectationWork({
      files: [{ path: 'plans/report.md' }],
      markers: [{ path: '_output', oneOf: ['<promise>done</promise>', '<promise>unfinished</promise>'] }],
    })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    const output = result.output as Record<string, unknown>
    // The agent facts stay alongside the typed evaluation.
    expect(output.kind).toBe('opencode')
    expect(output.text).toBe('the plan is written\n<promise>done</promise>')
    const expectation = output.expectation as Record<string, unknown>
    expect(expectation.satisfied).toBe(false)
    expect(expectation.matched).toBe('<promise>done</promise>')
    expect(expectation.missingFiles).toEqual([{ path: `${workDir}/plans/report.md` }])
    expect(expectation.missingMarkers).toEqual([])
    expect(expectation.failIfMatches).toEqual([])
    expect(expectation.message).toContain('missing required file')
  })

  it('reports a satisfied evaluation when every expect fact holds', async () => {
    const resources = createDefaultRunnerTestResources()
    await withTestRunnerResources(async () => {
      const runtime = makeFakeRuntime()
      const connection = makeFakeConnection()
      const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))
      runtime.setTurnResult({
        ok: true,
        value: {
          facts: {
            finalAssistantText: '<promise>done</promise>',
            runtimeSessionId: 'ses_expect_ok',
            workDir,
          },
          diagnostics: [],
        },
        diagnostics: [],
      })
      await resources.fileSystem.ensureDir(`${workDir}/plans`)
      await resources.fileSystem.writeText(`${workDir}/plans/report.md`, 'the plan')

      const work = expectationWork({
        files: [{ path: 'plans/report.md' }],
        markers: [
          { path: 'plans/report.md', contains: 'the plan' },
          { path: '_output', oneOf: ['<promise>done</promise>'] },
        ],
      })
      const result = await executor.execute(work, new AbortController().signal)

      expect(result.status).toBe('completed')
      const output = result.output as Record<string, unknown>
      const expectation = output.expectation as Record<string, unknown>
      expect(expectation.satisfied).toBe(true)
      expect(expectation.matched).toBe('<promise>done</promise>')
      expect(expectation.missingFiles).toEqual([])
      expect(expectation.missingMarkers).toEqual([])
      expect(expectation.failIfMatches).toEqual([])
      expect(expectation.message).toBe('Workflow completion requirements satisfied')
    }, resources)
  })

  it('keeps the AgentJob terminal verdict completed when the Workflow expectation is unsatisfied', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))
    runtime.setTurnResult({
      ok: true,
      value: {
        facts: {
          finalAssistantText: '<promise>done</promise>',
          runtimeSessionId: 'ses_expect_failif',
          workDir,
        },
        diagnostics: [],
      },
      diagnostics: [],
    })

    const work = expectationWork({
      files: [{ path: 'plans/missing-report.md' }],
      markers: [{ path: '_output', oneOf: ['<promise>done</promise>'], failIf: '<promise>done</promise>' }],
    })
    const result = await executor.execute(work, new AbortController().signal)

    // The AgentJob terminal verdict stays Completed: an unsatisfied
    // Workflow expectation is a Workflow completion fact, not an agent
    // execution failure.
    expect(result.status).toBe('completed')
    expect(result.exitCode).toBe(0)
    const output = result.output as Record<string, unknown>
    const expectation = output.expectation as Record<string, unknown>
    expect(expectation.satisfied).toBe(false)
    expect(expectation.missingFiles).toEqual([{ path: `${workDir}/plans/missing-report.md` }])
    expect(expectation.failIfMatches).toEqual([
      { marker: '<promise>done</promise>', failIf: '<promise>done</promise>', path: '_output' },
    ])
    expect(expectation.message).toContain('Workflow completion requirements were not satisfied')
  })

  it('reports missing file markers as typed facts', async () => {
    const resources = createDefaultRunnerTestResources()
    await withTestRunnerResources(async () => {
      const runtime = makeFakeRuntime()
      const connection = makeFakeConnection()
      const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))
      runtime.setTurnResult({
        ok: true,
        value: {
          facts: {
            finalAssistantText: 'no promise marker',
            runtimeSessionId: 'ses_expect_marker',
            workDir,
          },
          diagnostics: [],
        },
        diagnostics: [],
      })

      const work = expectationWork({
        markers: [{ path: '_output', oneOf: ['<promise>done</promise>'] }],
      })
      const result = await executor.execute(work, new AbortController().signal)

      const output = result.output as Record<string, unknown>
      const expectation = output.expectation as Record<string, unknown>
      expect(expectation.satisfied).toBe(false)
      expect(expectation.matched).toBeNull()
      expect(expectation.missingMarkers).toEqual([{ path: '_output', contains: '<promise>done</promise>' }])
    }, resources)
  })

  it('does not touch the result when the dispatch carries no expect', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))

    const work = buildAgentJobWork()
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('completed')
    const output = result.output as Record<string, unknown>
    expect('expectation' in output).toBe(false)
  })

  it('does not evaluate a failed turn', async () => {
    const runtime = makeFakeRuntime()
    const connection = makeFakeConnection()
    const executor = new AgentJobExecutor(connection.connection, makeAccessors(runtime.runtime))
    runtime.setTurnResult({
      ok: false,
      error: { kind: 'turn-failed', message: 'the runtime exploded', diagnostics: [] },
      diagnostics: [],
    })

    const work = expectationWork({ files: [{ path: 'plans/report.md' }] })
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe('failed')
    const output = result.output as Record<string, unknown>
    expect('expectation' in output).toBe(false)
  })
})
