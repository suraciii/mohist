import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { describe, expect, it, vi } from 'vitest'
import { ServerConnection } from '../src/server/connection.js'
import { validateDispatchEnvelope } from '../src/server/connection-dispatch.js'
import { executeAndTransition, reportOnce, type HostExecutionContext } from '../src/runtime/host-execution.js'
import { AgentJobExecutor } from '../src/runtime/agent-job-executor.js'
import { WorkExecutor } from '../src/runtime/executor.js'
import { PUBLISHED_SLACK_SKILL_NAME, PUBLISHED_SLACK_SKILL_VERSION } from '../src/runtime/slack-execution-context.js'
import type { DispatchWorkItem, PolledDispatch, RunnerOptions, WorkDispatchResponse } from '../src/core/types.js'
import type { HostTaskLogDeps } from '../src/runtime/host-task-log.js'
import type { AwaitingAckEntry, InFlightEntry } from '../src/runtime/host-state.js'
import { transportFetch, withFakeTransport } from './support/fake-transport.js'
import type {
  FollowupParams,
  JsonRpcErrorResponse,
  JsonRpcNotification,
  JsonRpcRequest,
  JsonRpcSuccessResponse,
  SessionCommandRequest,
  SessionStopParams,
  WorkflowRunStatusNotification,
  WorkspaceCommitDiffParams,
  WorkspaceFileContentParams,
  WorkspaceQueryParams,
} from '../src/contracts/runner-control.js'

const requestMethods = [
  'workspace.diff',
  'workspace.commits',
  'workspace.commit-diff',
  'workspace.status',
  'workspace.file-content',
  'workspace.remove',
  'session.followup',
  'session.stop',
  'session.command',
] as const

const standardErrors = new Map([
  [-32700, 'Parse error'],
  [-32600, 'Invalid Request'],
  [-32601, 'Method not found'],
  [-32602, 'Invalid params'],
  [-32603, 'Internal error'],
  [-32001, 'Response too large'],
])

interface FixtureEntry {
  method: (typeof requestMethods)[number]
  request: unknown
  success: unknown
  nullableSuccess?: unknown
  error: unknown
}

interface FixtureCatalog {
  requests: FixtureEntry[]
  notifications: Array<{ method: string; notification: unknown }>
}

describe('runner control JSON contract', () => {
  it('covers every request, result, nullable result, and specified error', () => {
    const catalog = readCatalog()
    expect(catalog.requests.map((entry) => entry.method)).toEqual(requestMethods)
    expect(catalog.requests.filter((entry) => entry.nullableSuccess).map((entry) => entry.method)).toEqual([
      'workspace.diff',
      'workspace.commits',
      'workspace.commit-diff',
    ])

    for (const entry of catalog.requests) assertEntry(entry)
    expect(new Set(catalog.requests.map((entry) => error(entry.error).error.code))).toEqual(
      new Set(standardErrors.keys()),
    )
  })

  it('uses the named params shapes consumed by the Runner', () => {
    const entries = new Map(readCatalog().requests.map((entry) => [entry.method, entry]))
    const query = request<WorkspaceQueryParams>(entries.get('workspace.diff')!).params.query
    const commit = request<WorkspaceCommitDiffParams>(entries.get('workspace.commit-diff')!).params
    const file = request<WorkspaceFileContentParams>(entries.get('workspace.file-content')!).params
    const followup = request<FollowupParams>(entries.get('session.followup')!).params
    const stop = request<SessionStopParams>(entries.get('session.stop')!).params
    const command = request<SessionCommandRequest>(entries.get('session.command')!).params

    expect(query).toMatchObject({ workflowRunId: 'run_101', issueNumber: 657, baseBranch: 'main' })
    expect(commit).toMatchObject({ hash: 'def4567890', query })
    expect(file).toMatchObject({ path: 'src/control.ts', query })
    expect(followup).toMatchObject({ operationId: 'operation_followup_1', turnId: 'turn_followup_1' })
    expect(stop).toMatchObject({ sessionId: 'session_1', turnId: 'turn_stop_1', operationId: 'operation_stop_1' })
    expect(command).toMatchObject({ command: 'reset', operationId: 'operation_command_1' })
  })

  it('covers the workflow status notification', () => {
    const entry = readCatalog().notifications[0]!
    const notification = entry.notification as JsonRpcNotification<WorkflowRunStatusNotification>

    expect(entry.method).toBe('workflow.status-changed')
    expect(notification).toEqual({
      jsonrpc: '2.0',
      method: 'workflow.status-changed',
      params: { workflowRunId: 'run_101', status: 'Completed' },
    })
    expect(notification).not.toHaveProperty('id')
  })
})

function assertEntry(entry: FixtureEntry): void {
  const rpcRequest = request<Record<string, unknown>>(entry)
  const rpcSuccess = entry.success as JsonRpcSuccessResponse<unknown>
  const rpcError = error(entry.error)

  expect(rpcRequest).toMatchObject({ jsonrpc: '2.0', method: entry.method })
  expect(rpcRequest.id).not.toBe('')
  expect(Array.isArray(rpcRequest.params)).toBe(false)
  expect(rpcSuccess).toMatchObject({ jsonrpc: '2.0', id: rpcRequest.id })
  expect(rpcSuccess).toHaveProperty('result')
  expect(rpcError.error.message).toBe(standardErrors.get(rpcError.error.code))
  expect(rpcError.id === null || rpcError.id === rpcRequest.id).toBe(true)

  if (entry.nullableSuccess) {
    expect(entry.nullableSuccess).toEqual({ jsonrpc: '2.0', id: rpcRequest.id, result: null })
  }
}

function request<TParams>(entry: FixtureEntry): JsonRpcRequest<TParams> {
  return entry.request as JsonRpcRequest<TParams>
}

function error(value: unknown): JsonRpcErrorResponse {
  return value as JsonRpcErrorResponse
}

function readCatalog(): FixtureCatalog {
  const url = new URL('../../../fixtures/runner-control.json', import.meta.url)
  return JSON.parse(readFileSync(url, 'utf8')) as FixtureCatalog
}

const contractOptions: RunnerOptions = {
  serverUrl: 'https://runner.test',
  runnerId: 'runner-contract',
  runnerRoot: '/virtual/runner-contract',
  pollIntervalMs: 100,
  heartbeatIntervalMs: 1_000,
  dispatchLivenessProbeIntervalMs: 1_000,
}

const contractPollReport = {
  processGeneration: 'contract-generation',
  inFlight: [],
  awaitingAck: [],
  admissionReady: true,
}

const contractSignal = new AbortController().signal

function contractIt(name: string, body: () => Promise<void>): void {
  it(name, async () => await withFakeTransport(async () => await body()))
}

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

function agentJobDispatch(overrides: Partial<WorkDispatchResponse> = {}): WorkDispatchResponse {
  return {
    workflowRunId: 'workflow-1',
    workId: 'agent-work-1',
    actionAttemptId: 'agent-attempt-1',
    workType: 'task',
    stage: 'execute',
    title: 'Agent work',
    uses: null,
    with: JSON.stringify({
      prompt: 'do the agent work',
      instructions: 'be careful',
      runtime: 'opencode',
      executionSource: 'non-slack',
      model: 'model-1',
      variant: 'fast',
    }),
    expect: JSON.stringify({ markers: [{ path: '_output', contains: 'done' }] }),
    variables: JSON.stringify({
      workspace: { name: 'pay' },
      repository: {
        name: 'server',
        gitUrl: 'https://example.test/server.git',
        baseBranch: 'main',
      },
      issue: { number: 684 },
    }),
    projectId: 'project-1',
    issueNumber: 684,
    epicNumber: 68,
    parentIssueContext: { title: 'Parent issue', body: 'Parent body' },
    artifacts: JSON.stringify({ files: [{ path: 'notes.txt' }] }),
    setVars: JSON.stringify({ result: 'done' }),
    ownerKind: 'agent-job',
    agentJobId: 'agent-job-1',
    agentSessionId: 'session-1',
    recovery: JSON.stringify({ budget: 1 }),
    recoveryRemaining: 1,
    agentDefinition: {
      instructions: 'be careful',
      runtime: 'opencode',
      model: 'model-1',
      variant: 'fast',
      skills: ['repo-guide'],
    },
    agentSessionStartup: {
      projectId: 'project-1',
      sessionId: 'session-1',
      allowedSubagents: [],
      spawnCommand: 'spawn-agent',
      workDir: '/virtual/workspace',
    },
    initialInputId: 'input-1',
    initialTurnId: 'turn-1',
    capabilityRevision: 'catalog-1',
    ...overrides,
  }
}

function workflowDispatch(): WorkDispatchResponse {
  return {
    workflowRunId: 'workflow-2',
    workId: 'workflow-work-1',
    workType: 'task',
    stage: 'build',
    title: 'Workflow work',
    uses: 'spec/task',
    with: JSON.stringify({ prompt: 'run the workflow action' }),
    expect: null,
    variables: JSON.stringify({
      executionSource: 'non-slack',
      workspace: { path: '/virtual/workflow-2' },
    }),
    projectId: 'project-1',
    issueNumber: 685,
    ownerKind: 'workflow',
  }
}

function managerAgentJobDispatch(overrides: Partial<WorkDispatchResponse> = {}): WorkDispatchResponse {
  const dispatch = agentJobDispatch()
  const payload = JSON.parse(dispatch.with ?? '{}') as Record<string, unknown>
  payload.runtime = 'pi'
  payload.executionSource = 'slack'
  payload.slackExecutionContext = slackExecutionContext()
  return {
    ...dispatch,
    ...overrides,
    workflowRunId: '',
    projectId: '__mohist_slack_manager__',
    with: JSON.stringify(payload),
    variables: null,
    agentDefinition: { ...dispatch.agentDefinition!, runtime: 'pi' },
  }
}

function slackExecutionContext() {
  const instructions = readFileSync(
    new URL(
      '../../server/src/Mohist.Server/Agent/Services/Assets/mohist-slack-collaboration.skill.md',
      import.meta.url,
    ),
    'utf8',
  )
  return {
    version: 1,
    replyAnchor: {
      workspaceId: 'T_MANAGER',
      conversationId: 'C_MANAGER',
      threadRootMessageId: '100.0',
      triggeringMessageId: '101.0',
      initiatingMemberId: 'U_MANAGER',
      connectionId: 'connection-manager',
      sessionId: 'session-manager',
      dispatchRef: 'slack:session-manager:input-1',
    },
    collaborationSkill: {
      name: PUBLISHED_SLACK_SKILL_NAME,
      version: PUBLISHED_SLACK_SKILL_VERSION,
      instructions,
      contentHash: createHash('sha256').update(instructions, 'utf8').digest('hex'),
    },
  }
}

function managerGrant(executionId: string) {
  return {
    managementCredential: `management-${executionId}`,
    replyCredential: `reply-${executionId}`,
    executionId,
    expiresAt: '2099-01-01T00:00:00.000Z',
    deploymentEpoch: 'epoch-1',
  }
}

async function poll(
  connection: ServerConnection,
  dispatches: readonly WorkDispatchResponse[],
): Promise<PolledDispatch[]> {
  transportFetch.mockResolvedValueOnce(jsonResponse({ dispatches }))
  return await connection.poll(contractSignal, contractPollReport)
}

interface ExecutionHarness {
  readonly context: HostExecutionContext
  readonly key: string
  readonly runtimeAccessors: ReturnType<typeof vi.fn>[]
  readonly workspacePrepare: ReturnType<typeof vi.fn>
  readonly namedMaterialize: ReturnType<typeof vi.fn>
  readonly namedMaterializeForIssue: ReturnType<typeof vi.fn>
  readonly workExecutorRef: ReturnType<typeof vi.fn>
  readonly currentCatalogRevision: ReturnType<typeof vi.fn>
}

function executionHarness(connection: ServerConnection, work: DispatchWorkItem): ExecutionHarness {
  const key = `${work.ownerKind ?? 'unknown'}:${work.agentJobId ?? work.workflowRunId}:${work.workId}`
  const inFlight = new Map<string, InFlightEntry>()
  const awaitingAck = new Map<string, { work: DispatchWorkItem; entry: AwaitingAckEntry }>()
  const entry: InFlightEntry = {
    done: Promise.resolve(),
    work,
    controller: new AbortController(),
  }
  inFlight.set(key, entry)

  const openCodeAccessor = vi.fn(() => null)
  const piAccessor = vi.fn(() => null)
  const workspacePrepare = vi.fn()
  const namedMaterialize = vi.fn()
  const namedMaterializeForIssue = vi.fn()
  const namedWorkspaceManager = {
    materialize: namedMaterialize,
    materializeForIssue: namedMaterializeForIssue,
  }
  const agentJobExecutor = new AgentJobExecutor(
    connection,
    { openCode: openCodeAccessor as never, pi: piAccessor as never },
    null,
    undefined,
    namedWorkspaceManager as never,
  )
  const workExecutor = new WorkExecutor(
    {} as never,
    { prepare: workspacePrepare } as never,
    connection,
    null,
    undefined,
    null,
    agentJobExecutor,
    null,
    undefined,
    null,
    undefined,
    namedWorkspaceManager as never,
  )
  const workExecutorRef = vi.fn(() => workExecutor)
  const currentCatalogRevision = vi.fn(() => null)
  const taskLogDeps = vi.fn((): HostTaskLogDeps => {
    throw new Error('invalid envelopes must not create task-log dependencies')
  })

  const context: HostExecutionContext = {
    options: contractOptions,
    connection,
    taskLogDeps,
    workExecutorRef,
    syncOpenCodeWorkOwners: vi.fn(),
    inFlight,
    awaitingAck,
    currentCatalogRevision,
    managerExecutionFor: vi.fn(() => null),
    releaseManagerExecution: vi.fn(async () => undefined),
  }
  return {
    context,
    key,
    runtimeAccessors: [openCodeAccessor, piAccessor],
    workspacePrepare,
    namedMaterialize,
    namedMaterializeForIssue,
    workExecutorRef,
    currentCatalogRevision,
  }
}

function withoutAgentField(field: string, value?: string): WorkDispatchResponse {
  const dispatch = structuredClone(agentJobDispatch())
  const withPayload = JSON.parse(dispatch.with ?? '{}') as Record<string, unknown>
  const variables = JSON.parse(dispatch.variables ?? '{}') as Record<string, unknown>

  if (field === 'executionSource') {
    delete withPayload.executionSource
  } else if (field === 'runtime') {
    if (value === undefined) {
      delete withPayload.runtime
      delete dispatch.agentDefinition
    } else withPayload.runtime = value
  } else if (field === 'ownerKind' || field === 'projectId' || field === 'agentJobId') {
    delete (dispatch as unknown as Record<string, unknown>)[field]
  } else if (field === 'owner-id-mismatch') {
    dispatch.ownerKind = 'workflow'
    dispatch.uses = 'mohist/opencode'
  } else if (field === 'repository') {
    delete variables.repository
  } else if (field === 'slackExecutionContext') {
    withPayload.executionSource = 'slack'
    delete withPayload.slackExecutionContext
  } else if (field === 'workspace') {
    delete variables.workspace
  }

  dispatch.with = JSON.stringify(withPayload)
  dispatch.variables = JSON.stringify(variables)
  return dispatch
}

const invalidEnvelopeVectors: ReadonlyArray<{
  name: string
  field: string
  dispatch: () => WorkDispatchResponse
}> = [
  {
    name: 'executionSource absent',
    field: 'executionSource',
    dispatch: () => withoutAgentField('executionSource'),
  },
  {
    name: 'ownerKind absent',
    field: 'ownerKind',
    dispatch: () => withoutAgentField('ownerKind'),
  },
  {
    name: 'owner id does not match ownerKind',
    field: 'agentJobId',
    dispatch: () => withoutAgentField('owner-id-mismatch'),
  },
  {
    name: 'projectId absent',
    field: 'projectId',
    dispatch: () => withoutAgentField('projectId'),
  },
  {
    name: 'runtime absent',
    field: 'runtime',
    dispatch: () => withoutAgentField('runtime'),
  },
  {
    name: 'runtime unknown',
    field: 'runtime',
    dispatch: () => withoutAgentField('runtime', 'unknown-runtime'),
  },
  {
    name: 'named-workspace repository triple absent',
    field: 'repository',
    dispatch: () => withoutAgentField('repository'),
  },
  {
    name: 'Slack source context absent',
    field: 'slackExecutionContext',
    dispatch: () => withoutAgentField('slackExecutionContext'),
  },
  {
    name: 'workspace binding absent',
    field: 'workspace',
    dispatch: () => withoutAgentField('workspace'),
  },
]

describe('runner control strict envelope contract', () => {
  contractIt(
    'polls Workflow and AgentJob envelopes through ServerConnection and keeps grant metadata outside work',
    async () => {
      const connection = new ServerConnection(contractOptions)
      const grant = managerGrant('execution-1')
      const agentResponse = managerAgentJobDispatch({
        managerExecutionGrant: grant,
        originMarker: 'slack-manager',
      })

      const polled = await poll(connection, [workflowDispatch(), agentResponse])
      expect(polled).toHaveLength(2)

      const workflow = polled[0]!
      expect(workflow.work).toMatchObject({
        workflowRunId: 'workflow-2',
        workId: 'workflow-work-1',
        workType: 'task',
        stage: 'build',
        title: 'Workflow work',
        uses: 'spec/task',
        projectId: 'project-1',
        issueNumber: 685,
        ownerKind: 'workflow',
        with: { prompt: 'run the workflow action' },
        variables: { workspace: { path: '/virtual/workflow-2' } },
      })
      expect(validateDispatchEnvelope(workflow.work)).toBeUndefined()
      expect(workflow).not.toHaveProperty('managerExecutionGrant')
      expect(workflow).not.toHaveProperty('originMarker')

      const agent = polled[1]!
      expect(agent.work).toMatchObject({
        workflowRunId: '',
        workId: 'agent-work-1',
        actionAttemptId: 'agent-attempt-1',
        workType: 'task',
        stage: 'execute',
        title: 'Agent work',
        uses: null,
        projectId: '__mohist_slack_manager__',
        issueNumber: 684,
        epicNumber: 68,
        ownerKind: 'agent-job',
        agentJobId: 'agent-job-1',
        agentSessionId: 'session-1',
        with: {
          prompt: 'do the agent work',
          instructions: 'be careful',
          runtime: 'pi',
          executionSource: 'slack',
          slackExecutionContext: expect.any(Object),
        },
        variables: null,
        expect: { markers: [{ path: '_output', contains: 'done' }] },
        artifacts: { files: [{ path: 'notes.txt' }] },
        setVars: { result: 'done' },
        recovery: { budget: 1 },
        recoveryRemaining: 1,
        initialInputId: 'input-1',
        initialTurnId: 'turn-1',
        capabilityRevision: 'catalog-1',
      })
      expect(agent.work.parentIssueContext).toEqual({ title: 'Parent issue', body: 'Parent body' })
      expect(agent.work.agentDefinition).toEqual({
        instructions: 'be careful',
        runtime: 'pi',
        model: 'model-1',
        variant: 'fast',
        skills: ['repo-guide'],
      })
      expect(agent.work.agentSessionStartup).toMatchObject({
        projectId: 'project-1',
        sessionId: 'session-1',
        spawnCommand: 'spawn-agent',
      })
      expect(validateDispatchEnvelope(agent.work)).toBeUndefined()
      expect(agent.managerExecutionGrant).toEqual(grant)
      expect(agent.originMarker).toBe('slack-manager')
      expect(agent.work).not.toHaveProperty('managerExecutionGrant')
      expect(agent.work).not.toHaveProperty('originMarker')
    },
  )

  it.each([
    {
      name: 'missing grant',
      dispatch: () => managerAgentJobDispatch({ managerExecutionGrant: null, originMarker: 'slack-manager' }),
    },
    {
      name: 'malformed grant',
      dispatch: () => managerAgentJobDispatch({ managerExecutionGrant: {} as never, originMarker: 'slack-manager' }),
    },
    {
      name: 'missing origin marker',
      dispatch: () =>
        managerAgentJobDispatch({ managerExecutionGrant: managerGrant('missing-origin'), originMarker: null }),
    },
    {
      name: 'mismatched origin marker',
      dispatch: () =>
        managerAgentJobDispatch({ managerExecutionGrant: managerGrant('wrong-origin'), originMarker: 'other-origin' }),
    },
    {
      name: 'grant attached to a non-Manager dispatch',
      dispatch: () =>
        agentJobDispatch({ managerExecutionGrant: managerGrant('wrong-owner'), originMarker: 'slack-manager' }),
    },
  ])('$name Manager metadata settles through report/ack without execution', async ({ dispatch: buildDispatch }) => {
    await withFakeTransport(async () => {
      const connection = new ServerConnection(contractOptions)
      const [polled] = await poll(connection, [buildDispatch()])
      const work = polled!.work
      const harness = executionHarness(connection, work)

      expect(polled!.validationFailure).toMatchObject({
        status: 'failed',
        error: { code: 'invalid-dispatch' },
      })
      transportFetch.mockResolvedValueOnce(jsonResponse({ verdict: 'accepted' }))
      await executeAndTransition(
        harness.context,
        work,
        contractSignal,
        harness.key,
        harness.context.inFlight.get(harness.key)!,
        polled!.validationFailure!,
      )

      expect(transportFetch).toHaveBeenCalledTimes(2)
      expect(transportFetch.mock.calls[1]![0]).toContain('/report')
      const reportBody = JSON.parse((transportFetch.mock.calls[1]![1] as RequestInit).body as string) as Record<
        string,
        unknown
      >
      expect(reportBody.status).toBe('failed')
      expect(reportBody.error).toMatchObject({ code: 'invalid-dispatch' })
      expect(harness.workExecutorRef).not.toHaveBeenCalled()
      expect(harness.runtimeAccessors[0]).not.toHaveBeenCalled()
      expect(harness.runtimeAccessors[1]).not.toHaveBeenCalled()
      expect(harness.context.awaitingAck.size).toBe(0)
    })
  })

  it.each(invalidEnvelopeVectors)(
    'rejects $name before runtime, named workspace, or workflow workspace startup',
    async ({ field, dispatch: buildDispatch }) => {
      await withFakeTransport(async () => {
        const connection = new ServerConnection(contractOptions)
        const workDispatch = buildDispatch()
        const [polled] = await poll(connection, [workDispatch])
        const work = polled!.work
        const harness = executionHarness(connection, work)

        const validation = validateDispatchEnvelope(work)
        expect(validation).toMatchObject({
          status: 'failed',
          error: { code: 'invalid-dispatch' },
        })
        expect(validation?.message).toContain(field.split('.')[0])

        transportFetch.mockResolvedValueOnce(jsonResponse({ verdict: 'accepted' }))
        await executeAndTransition(
          harness.context,
          work,
          contractSignal,
          harness.key,
          harness.context.inFlight.get(harness.key)!,
        )

        expect(transportFetch).toHaveBeenCalledTimes(2)
        const reportCall = transportFetch.mock.calls[1]!
        const reportBody = JSON.parse((reportCall[1] as RequestInit).body as string) as Record<string, unknown>
        expect(reportBody.status).toBe('failed')
        expect(reportBody.error).toMatchObject({ code: 'invalid-dispatch' })
        expect(reportBody.status).not.toBe('completed')
        expect(harness.runtimeAccessors[0]).not.toHaveBeenCalled()
        expect(harness.runtimeAccessors[1]).not.toHaveBeenCalled()
        expect(harness.currentCatalogRevision).not.toHaveBeenCalled()
        expect(harness.workExecutorRef).not.toHaveBeenCalled()
        expect(harness.workspacePrepare).not.toHaveBeenCalled()
        expect(harness.namedMaterialize).not.toHaveBeenCalled()
        expect(harness.namedMaterializeForIssue).not.toHaveBeenCalled()
      })
    },
  )

  contractIt('keeps Manager grants isolated per polled dispatch and across poll calls', async () => {
    const connection = new ServerConnection(contractOptions)
    const firstGrant = managerGrant('execution-first')
    const secondGrant = managerGrant('execution-second')
    const first = managerAgentJobDispatch({
      workId: 'agent-work-first',
      managerExecutionGrant: firstGrant,
      originMarker: 'slack-manager',
    })
    const second = managerAgentJobDispatch({
      workId: 'agent-work-second',
      managerExecutionGrant: secondGrant,
      originMarker: 'slack-manager',
    })
    const later = agentJobDispatch({ workId: 'agent-work-later' })

    transportFetch.mockResolvedValueOnce(jsonResponse({ dispatches: [first, second] }))
    transportFetch.mockResolvedValueOnce(jsonResponse({ dispatches: [later] }))
    const firstPoll = await connection.poll(contractSignal, contractPollReport)
    const secondPoll = await connection.poll(contractSignal, contractPollReport)

    expect(firstPoll[0]?.managerExecutionGrant).toEqual(firstGrant)
    expect(firstPoll[1]?.managerExecutionGrant).toEqual(secondGrant)
    expect(firstPoll[0]?.managerExecutionGrant).not.toEqual(firstPoll[1]?.managerExecutionGrant)
    expect(firstPoll[0]?.work.workId).toBe('agent-work-first')
    expect(firstPoll[1]?.work.workId).toBe('agent-work-second')
    expect(secondPoll[0]?.work.workId).toBe('agent-work-later')
    expect(secondPoll[0]).not.toHaveProperty('managerExecutionGrant')
    expect(secondPoll[0]).not.toHaveProperty('originMarker')

    const connectionSource = readFileSync(new URL('../src/server/connection.ts', import.meta.url), 'utf8')
    expect(connectionSource).not.toContain('lastPolledDispatches')
    expect(connectionSource).not.toContain('takeLastPolledDispatches')
  })

  contractIt(
    'reports an invalid envelope once through awaitingAck and does not re-report it on the next poll',
    async () => {
      const connection = new ServerConnection(contractOptions)
      const [polled] = await poll(connection, [withoutAgentField('executionSource')])
      const work = polled!.work
      const harness = executionHarness(connection, work)

      transportFetch.mockResolvedValueOnce(jsonResponse({ verdict: 'accepted' }))
      await executeAndTransition(
        harness.context,
        work,
        contractSignal,
        harness.key,
        harness.context.inFlight.get(harness.key)!,
      )

      expect(harness.context.awaitingAck.size).toBe(0)
      const reportCallsAfterExecution = transportFetch.mock.calls.filter(([input]) => String(input).endsWith('/report'))
      expect(reportCallsAfterExecution).toHaveLength(1)
      const reportBody = JSON.parse((reportCallsAfterExecution[0]![1] as RequestInit).body as string) as Record<
        string,
        unknown
      >
      expect(reportBody.status).toBe('failed')
      expect(reportBody.error).toMatchObject({ code: 'invalid-dispatch' })

      await reportOnce(harness.context, harness.key)
      expect(transportFetch.mock.calls.filter(([input]) => String(input).endsWith('/report'))).toHaveLength(1)

      transportFetch.mockResolvedValueOnce(jsonResponse({ dispatches: [] }))
      await expect(
        connection.poll(contractSignal, {
          ...contractPollReport,
          inFlight: [...harness.context.inFlight.keys()],
          awaitingAck: [...harness.context.awaitingAck.keys()],
        }),
      ).resolves.toEqual([])
      expect(harness.context.awaitingAck).not.toHaveProperty(harness.key)
      expect(transportFetch.mock.calls.filter(([input]) => String(input).endsWith('/report'))).toHaveLength(1)

      const nextPollBody = JSON.parse((transportFetch.mock.calls.at(-1)![1] as RequestInit).body as string) as {
        inFlight: string[]
        awaitingAck: string[]
      }
      expect(nextPollBody.inFlight).toEqual([])
      expect(nextPollBody.awaitingAck).toEqual([])
    },
  )
})
