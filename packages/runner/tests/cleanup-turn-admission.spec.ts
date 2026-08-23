import { describe, expect, it, vi } from 'vitest'
import { buildActionHost, type ExecutorCapabilityDeps } from '../src/runtime/executor-capabilities.js'
import { piAction } from '../src/actions/pi.js'
import { workflowCleanupOperationId } from '../src/actions/workflow-agent-session-reporter.js'
import type { DispatchWorkItem } from '../src/core/types.js'
import {
  CleanupPredecessorDeliveryWaitTimeoutError,
  type AgentSessionRuntimeEventOutbox,
  type RuntimeEventRecord,
} from '../src/server/runtime-event-outbox.js'
import { SkillResolver } from '../src/runtime/skill-resolver.js'

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  const promise = new Promise<T>((res) => {
    resolve = res
  })
  return { promise, resolve }
}

function outbox(): AgentSessionRuntimeEventOutbox {
  const records: RuntimeEventRecord[] = []
  return {
    ready: () => true,
    load: async () => {},
    recover: async () => {},
    enqueueBeforeExecution: async (record) => {
      records.push(record as RuntimeEventRecord)
    },
    awaitInputReceipt: async (recordId) => {
      const record = records.find((candidate) => candidate.id === recordId)
      const payload = record?.event.payload ?? {}
      return {
        type: 'session.input',
        inputDeliveryId: typeof payload.inputDeliveryId === 'string' ? payload.inputDeliveryId : recordId,
        agentTurnId: typeof payload.turnId === 'string' ? payload.turnId : 'turn-input-1',
        agentSessionId: 'agent-session-1',
      }
    },
    enqueueProducedFact: async (record) => {
      records.push(record as RuntimeEventRecord)
    },
    enqueueProducedFactBatch: async (batch) => {
      records.push(...(batch as RuntimeEventRecord[]))
    },
    kick: async () => {},
    stop: async () => {},
    snapshot: () => [...records],
  }
}

function work(overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: 'workflow-1',
    workId: 'work-1',
    taskRunId: 'task-1',
    workType: 'task',
    stage: 'build',
    title: 'Build',
    uses: 'mohist/opencode',
    projectId: 'project-1',
    with: { prompt: 'cleanup' },
    variables: {},
    ...overrides,
  }
}

function openConnection(open: ReturnType<typeof vi.fn>) {
  return {
    runnerId: 'runner-1',
    openWorkflowAgentSession: open,
    attachWorkflowAgentSession: vi.fn(async () => {}),
    resetWorkflowAgentSession: vi.fn(async () => {}),
    recoverMissingWorkflowAgentSession: vi.fn(async () => {}),
  } as never
}

function opencodeRuntime() {
  return {
    ready: () => true,
    diagnostic: () => null,
    createSession: vi.fn(),
    resolveSession: vi.fn(async () => ({ ok: true, value: { activeTurn: false }, diagnostics: [] })),
    runTurn: vi.fn(async () => ({
      ok: true as const,
      value: {
        facts: { runtimeSessionId: 'runtime-1', workDir: '/workspace', finalAssistantText: 'done' },
        diagnostics: [],
      },
      diagnostics: [],
    })),
  } as never
}

function opencodeDeps(
  connection: unknown,
  runtime: unknown,
  eventOutbox: AgentSessionRuntimeEventOutbox,
  wait: ReturnType<typeof vi.fn>,
): ExecutorCapabilityDeps {
  return {
    connection: connection as never,
    skillResolver: new SkillResolver(),
    piRuntime: null,
    openCodeRuntime: runtime as never,
    agentSessionRuntimeEventOutbox: eventOutbox,
    runtimeEventRecordId: (() => {
      let n = 0
      return () => `input-${++n}`
    })(),
    bindingRecoveryCoordinator: null,
    cleanupTerminalFactDeliveryBudgetMs: 60_000,
  }
}

describe('cleanup turn admission', () => {
  it('waits before OpenCode open and admits an active projected session after attempt-1 delivery', async () => {
    const gate = deferred<void>()
    const wait = vi.fn(async (..._args: any[]) => await gate.promise)
    const open = vi.fn(async () => ({
      sessionId: 'agent-session-1',
      runtimeSessionId: 'runtime-1',
      workDir: '/workspace',
      status: 'active',
    }))
    const runtime = opencodeRuntime() as any
    const eventOutbox = outbox()
    ;(eventOutbox as unknown as { awaitCleanupPredecessorDelivery: typeof wait }).awaitCleanupPredecessorDelivery = wait
    const item = work({ taskRunId: null })
    const host = buildActionHost(
      opencodeDeps(openConnection(open), runtime as any, eventOutbox, wait),
      item,
      '/workspace',
      new AbortController().signal,
      null as never,
      new Set(['agent-turn']),
      1,
    )

    const resultPromise = host.agent!.turn({ prompt: 'cleanup', session: 'work-1' })
    await Promise.resolve()
    expect(wait).toHaveBeenCalledWith(
      {
        projectId: 'project-1',
        workflowRunId: 'workflow-1',
        sessionName: 'work-1',
        cleanupAttempt: 1,
        precedingCleanupOperationId: null,
      },
      expect.objectContaining({ budgetMs: 60_000 }),
    )
    expect(open).not.toHaveBeenCalled()

    gate.resolve()
    const result = await resultPromise
    expect(result.error).toBeUndefined()
    expect(open).toHaveBeenCalledTimes(1)
    expect(runtime.runTurn).toHaveBeenCalledTimes(1)
  })

  it('waits for the immediately preceding cleanup operation on OpenCode attempt 2+', async () => {
    const wait = vi.fn(async (..._args: any[]) => {})
    const open = vi.fn(async () => ({
      sessionId: 'agent-session-1',
      runtimeSessionId: 'runtime-1',
      workDir: '/workspace',
      status: 'unknown',
    }))
    const eventOutbox = outbox()
    ;(eventOutbox as unknown as { awaitCleanupPredecessorDelivery: typeof wait }).awaitCleanupPredecessorDelivery = wait
    const item = work()
    const host = buildActionHost(
      opencodeDeps(openConnection(open), opencodeRuntime() as any, eventOutbox, wait),
      item,
      '/workspace',
      new AbortController().signal,
      null as never,
      new Set(['agent-turn']),
      2,
    )

    const result = await host.agent!.turn({ prompt: 'cleanup', session: 'work-1' })
    expect(result.error).toBeUndefined()
    expect(wait.mock.calls[0]?.[0]).toEqual({
      projectId: 'project-1',
      workflowRunId: 'workflow-1',
      sessionName: 'work-1',
      cleanupAttempt: 2,
      precedingCleanupOperationId: workflowCleanupOperationId('workflow-1', 'task-1', 'work-1', 1),
    })
    expect(open).toHaveBeenCalledTimes(1)
  })

  it('converts a delivery wait timeout into structured OpenCode cleanup failure evidence', async () => {
    const target = {
      projectId: 'project-1',
      workflowRunId: 'workflow-1',
      sessionName: 'work-1',
      cleanupAttempt: 1,
      precedingCleanupOperationId: null,
    }
    const wait = vi.fn(async (..._args: any[]) => {
      throw new CleanupPredecessorDeliveryWaitTimeoutError(target, 321)
    })
    const open = vi.fn()
    const eventOutbox = outbox()
    ;(eventOutbox as unknown as { awaitCleanupPredecessorDelivery: typeof wait }).awaitCleanupPredecessorDelivery = wait
    const item = work()
    const host = buildActionHost(
      opencodeDeps(openConnection(open), opencodeRuntime() as any, eventOutbox, wait),
      item,
      '/workspace',
      new AbortController().signal,
      null as never,
      new Set(['agent-turn']),
      1,
    )

    const result = await host.agent!.turn({ prompt: 'cleanup', session: 'work-1' })
    expect(result).toMatchObject({
      error: {
        code: 'session-delivery-wait-timeout',
        message: expect.stringContaining('project-1/workflow-1/work-1'),
      },
    })
    expect(result.error?.message).toContain('work item work-1')
    expect(result.error?.message).toContain('321ms')
    expect(open).not.toHaveBeenCalled()
  })

  it('keeps a non-cleanup turn fail-closed for an unsettled projected session', async () => {
    const wait = vi.fn(async (..._args: any[]) => {})
    const open = vi.fn(async () => ({
      sessionId: 'agent-session-1',
      runtimeSessionId: 'runtime-1',
      workDir: '/workspace',
      status: 'unknown',
    }))
    const eventOutbox = outbox()
    ;(eventOutbox as unknown as { awaitCleanupPredecessorDelivery: typeof wait }).awaitCleanupPredecessorDelivery = wait
    const host = buildActionHost(
      opencodeDeps(openConnection(open), opencodeRuntime() as any, eventOutbox, wait),
      work(),
      '/workspace',
      new AbortController().signal,
      null as never,
      new Set(['agent-turn']),
    )

    const result = await host.agent!.turn({ prompt: 'new task', session: 'work-1' })
    expect(result).toMatchObject({ error: { code: 'session-binding-failed' } })
    expect(result.error?.message).toContain('retry is fail-closed')
    expect(wait).not.toHaveBeenCalled()
  })

  it('waits before Pi opens the bound session and submits cleanup input', async () => {
    const gate = deferred<void>()
    const wait = vi.fn(async (..._args: any[]) => await gate.promise)
    const open = vi.fn(async () => ({
      sessionId: 'agent-session-1',
      runtimeSessionId: 'pi-runtime-1',
      runtime: 'pi',
      workDir: '/workspace',
    }))
    const eventOutbox = outbox()
    ;(eventOutbox as unknown as { awaitCleanupPredecessorDelivery: typeof wait }).awaitCleanupPredecessorDelivery = wait
    const runtime: any = {
      ready: () => true,
      diagnostic: () => null,
      createSession: vi.fn(),
      runTurn: vi.fn(async () => ({
        ok: true as const,
        value: {
          facts: { finalAssistantText: 'done', runtimeSessionId: 'pi-runtime-1', workDir: '/workspace' },
        },
        diagnostics: [],
      })),
    }
    const resultPromise = piAction({
      workflowRunId: 'workflow-1',
      workId: 'work-1',
      taskRunId: 'task-1',
      workType: 'task',
      workDir: '/workspace',
      signal: new AbortController().signal,
      projectId: 'project-1',
      with: { prompt: 'cleanup', session: 'custom-session' },
      piRuntime: runtime as never,
      serverConnection: openConnection(open),
      runtimeEventOutbox: eventOutbox,
      runtimeEventRecordId: (() => {
        let n = 0
        return () => `record-${++n}`
      })(),
      runnerId: 'runner-1',
      cleanupAttempt: 2,
      cleanupTerminalFactDeliveryBudgetMs: 123,
      preparedPrompt: 'cleanup',
      preparedOptions: {},
    })

    await Promise.resolve()
    expect(open).not.toHaveBeenCalled()
    gate.resolve()
    const result = await resultPromise
    expect(result.error).toBeUndefined()
    expect(wait.mock.calls[0]?.[0]).toEqual({
      projectId: 'project-1',
      workflowRunId: 'workflow-1',
      sessionName: 'custom-session',
      cleanupAttempt: 2,
      precedingCleanupOperationId: workflowCleanupOperationId('workflow-1', 'task-1', 'work-1', 1),
    })
    expect(wait.mock.calls[0]?.[1]).toMatchObject({ budgetMs: 123 })
    expect(open).toHaveBeenCalledTimes(1)
    expect(eventOutbox.snapshot().some((record) => record.event.type === 'session.cleanup')).toBe(true)
  })
})
