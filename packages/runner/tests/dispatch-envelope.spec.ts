import { describe, expect, it, vi } from 'vitest'
import type { DispatchWorkItem, RunnerOptions, WorkItemResult } from '../src/core/types.js'
import { validateDispatchEnvelope } from '../src/server/connection-dispatch.js'
import type { ServerConnection } from '../src/server/connection.js'
import { executeAndTransition, type HostExecutionContext } from '../src/runtime/host-execution.js'
import type { WorkExecutor } from '../src/runtime/executor.js'
import type { HostTaskLogDeps } from '../src/runtime/host-task-log.js'

const options: RunnerOptions = {
  serverUrl: 'https://runner.test',
  runnerId: 'runner-1',
  runnerRoot: '/virtual/runner',
  pollIntervalMs: 100,
  heartbeatIntervalMs: 1_000,
  dispatchLivenessProbeIntervalMs: 1_000,
}

function validAgentJobWork(): DispatchWorkItem {
  return {
    workflowRunId: 'workflow-1',
    workId: 'work-1',
    workType: 'task',
    ownerKind: 'agent-job',
    agentJobId: 'job-1',
    projectId: 'project-1',
    with: {
      prompt: 'run the task',
      runtime: 'opencode',
      executionSource: 'non-slack',
    },
    variables: {
      workspace: { name: 'workspace-1' },
      repository: {
        name: 'repo-1',
        gitUrl: 'https://example.test/repo-1.git',
        baseBranch: 'main',
      },
    },
  }
}

function contextFor(
  work: DispatchWorkItem,
  report: ReturnType<typeof vi.fn>,
): {
  context: HostExecutionContext
  key: string
  executorRef: ReturnType<typeof vi.fn>
  currentCatalogRevision: ReturnType<typeof vi.fn>
  taskLogDeps: ReturnType<typeof vi.fn>
} {
  const key = 'agent-job:job-1:work-1'
  const inFlight = new Map<string, { done: Promise<void>; work: DispatchWorkItem; controller: AbortController }>()
  const awaitingAck = new Map<
    string,
    { work: DispatchWorkItem; entry: { result: WorkItemResult; attempts: number; retryAt: number | null } }
  >()
  const entry = {
    done: Promise.resolve(),
    work,
    controller: new AbortController(),
  }
  inFlight.set(key, entry)

  const executorRef = vi.fn(() => ({}) as WorkExecutor)
  const currentCatalogRevision = vi.fn(() => null)
  const taskLogDeps = vi.fn((): HostTaskLogDeps => {
    throw new Error('invalid envelopes must not create task-log dependencies')
  })
  const context: HostExecutionContext = {
    options,
    connection: { report } as unknown as ServerConnection,
    taskLogDeps,
    workExecutorRef: executorRef,
    syncOpenCodeWorkOwners: vi.fn(),
    inFlight,
    awaitingAck,
    currentCatalogRevision,
    managerExecutionFor: vi.fn(() => null),
    releaseManagerExecution: vi.fn(async () => undefined),
  }
  return { context, key, executorRef, currentCatalogRevision, taskLogDeps }
}

function withoutField(work: DispatchWorkItem, field: string): DispatchWorkItem {
  const copy = structuredClone(work)
  if (field === 'executionSource' || field === 'runtime') {
    delete copy.with?.[field]
  } else if (field === 'ownerKind' || field === 'projectId' || field === 'agentJobId' || field === 'workflowRunId') {
    delete (copy as unknown as Record<string, unknown>)[field]
  } else {
    delete (copy.variables?.repository as Record<string, unknown>)[field]
  }
  return copy
}

type Vector = {
  name: string
  field: string
  work: () => DispatchWorkItem
}

const vectors: Vector[] = [
  {
    name: 'executionSource',
    field: 'executionSource',
    work: () => withoutField(validAgentJobWork(), 'executionSource'),
  },
  {
    name: 'ownerKind',
    field: 'ownerKind',
    work: () => withoutField(validAgentJobWork(), 'ownerKind'),
  },
  {
    name: 'agent job owner id',
    field: 'agentJobId',
    work: () => withoutField(validAgentJobWork(), 'agentJobId'),
  },
  {
    name: 'workflow owner id',
    field: 'workflowRunId',
    work: () => ({
      ...withoutField(validAgentJobWork(), 'workflowRunId'),
      ownerKind: 'workflow',
      agentJobId: null,
      uses: 'mohist/opencode',
    }),
  },
  {
    name: 'projectId',
    field: 'projectId',
    work: () => withoutField(validAgentJobWork(), 'projectId'),
  },
  {
    name: 'agent-job runtime',
    field: 'runtime',
    work: () => withoutField(validAgentJobWork(), 'runtime'),
  },
  {
    name: 'workflow runtime',
    field: 'runtime',
    work: () => ({
      ...validAgentJobWork(),
      ownerKind: 'workflow',
      agentJobId: null,
      uses: null,
    }),
  },
  {
    name: 'named workspace repository.name',
    field: 'repository.name',
    work: () => withoutField(validAgentJobWork(), 'name'),
  },
  {
    name: 'named workspace repository.gitUrl',
    field: 'repository.gitUrl',
    work: () => withoutField(validAgentJobWork(), 'gitUrl'),
  },
  {
    name: 'named workspace repository.baseBranch',
    field: 'repository.baseBranch',
    work: () => withoutField(validAgentJobWork(), 'baseBranch'),
  },
]

describe('dispatch envelope validation', () => {
  it('accepts a complete AgentJob envelope and resolves runtime from agentDefinition when needed', () => {
    const work = validAgentJobWork()
    expect(validateDispatchEnvelope(work)).toBeUndefined()

    const fromDefinition = validAgentJobWork()
    delete fromDefinition.with?.runtime
    fromDefinition.agentDefinition = {
      instructions: 'follow the task',
      runtime: 'pi',
      skills: [],
    }
    expect(validateDispatchEnvelope(fromDefinition)).toBeUndefined()
  })

  it.each(vectors)(
    'reports a failed result for missing $name through the ack path',
    async ({ field, work: buildWork }) => {
      const work = buildWork()
      let context!: HostExecutionContext
      let key = ''
      const report = vi.fn(async (..._args: unknown[]) => {
        const held = context.awaitingAck.get(key)
        expect(held?.entry.result).toMatchObject({
          status: 'failed',
          error: { code: 'invalid-dispatch' },
        })
        return { verdict: 'accepted' as const }
      })
      const setup = contextFor(work, report)
      context = setup.context
      key = setup.key
      const { executorRef, currentCatalogRevision, taskLogDeps } = setup

      const result = validateDispatchEnvelope(work)
      expect(result).toMatchObject({
        status: 'failed',
        message: expect.stringContaining(field.split('.')[0]),
        error: { code: 'invalid-dispatch' },
      })

      await executeAndTransition(context, work, new AbortController().signal, key, context.inFlight.get(key)!)

      expect(report).toHaveBeenCalledTimes(1)
      expect((report.mock.calls[0] as unknown[])[1]).toMatchObject({
        status: 'failed',
        error: { code: 'invalid-dispatch' },
      })
      expect(executorRef).not.toHaveBeenCalled()
      expect(currentCatalogRevision).not.toHaveBeenCalled()
      expect(taskLogDeps).not.toHaveBeenCalled()
      expect(context.awaitingAck.size).toBe(0)
    },
  )
})
