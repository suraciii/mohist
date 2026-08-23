import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  CleanupPredecessorDeliveryWaitTimeoutError,
  type RuntimeEventRecord,
} from '../src/server/runtime-event-outbox.js'
import {
  RecordingFileSystem,
  flushMicrotasks,
  makeOutbox,
  workflowFact,
} from './support/runtime-event-outbox-fixture.js'

const workflowTarget = {
  projectId: 'proj-1',
  workflowRunId: 'wf-1',
  sessionName: 'build',
} as const

function waitTarget(cleanupAttempt: number, precedingCleanupOperationId: string | null) {
  return { ...workflowTarget, cleanupAttempt, precedingCleanupOperationId }
}

beforeEach(() => vi.useFakeTimers())
afterEach(() => vi.useRealTimers())

describe('AgentSessionRuntimeEventOutbox — cleanup predecessor delivery wait', () => {
  it('completes immediately when no predecessor record is retained', async () => {
    const { outbox } = makeOutbox({})
    await outbox.load()

    await expect(
      outbox.awaitCleanupPredecessorDelivery?.(waitTarget(1, null), {
        budgetMs: 1000,
        signal: new AbortController().signal,
      }),
    ).resolves.toBeUndefined()
  })

  it('waits for every original-turn Workflow record and resolves after durable acknowledgement settlement', async () => {
    let release!: () => void
    let resolveDeliveryStarted!: () => void
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    const deliveryStarted = new Promise<void>((resolve) => {
      resolveDeliveryStarted = resolve
    })
    const { outbox } = makeOutbox({
      boundedConcurrency: 1,
      deliver: {
        async send() {
          return []
        },
        async sendBatch(records) {
          resolveDeliveryStarted()
          await gate
          return records.map((record) => [{ type: record.event.type }])
        },
      },
    })
    await outbox.load()
    await outbox.enqueueProducedFact(workflowFact('original-delta'))
    await outbox.enqueueProducedFact(
      workflowFact('original-idle', { event: { type: 'session.activity', payload: { activity: 'idle' } } }),
    )
    await deliveryStarted

    let settled = false
    const wait = outbox.awaitCleanupPredecessorDelivery?.(waitTarget(1, null), {
      budgetMs: 1000,
      signal: new AbortController().signal,
    })
    if (!wait) throw new Error('production outbox must expose cleanup predecessor delivery wait')
    wait.then(() => {
      settled = true
    })
    await flushMicrotasks()
    expect(settled).toBe(false)

    release()
    await expect(wait).resolves.toBeUndefined()
    expect(outbox.snapshot()).toEqual([])
  })

  it('keeps attempt 2 waiting for correlated Session facts after the Workflow cleanup boundary settles', async () => {
    const operationId = 'workflow-cleanup:wf-1:task-1.1:work-1:1'
    let releaseFollowup!: () => void
    let followupStarted!: () => void
    const followupGate = new Promise<void>((resolve) => {
      releaseFollowup = resolve
    })
    const started = new Promise<void>((resolve) => {
      followupStarted = resolve
    })
    const cleanupBoundary: RuntimeEventRecord = {
      ...workflowFact(operationId),
      id: operationId,
      producerFamily: 'workflow-cleanup',
      event: {
        type: 'session.cleanup',
        payload: {
          cleanupOperationId: operationId,
          inputDeliveryId: `workflow-cleanup-input:${operationId}`,
          turnId: `workflow-cleanup-turn:${operationId}`,
        },
      },
      work: { ...workflowFact(operationId).work!, agentTurnId: null },
    }
    const followupInput: RuntimeEventRecord = {
      id: `${operationId}:runtime-input`,
      producerFamily: 'session-followup',
      target: { kind: 'session', sessionId: 'agent-session-1' },
      runtimeSessionId: 'runtime-1',
      sessionTurnId: `workflow-cleanup-turn:${operationId}`,
      work: null,
      event: {
        type: 'session.input',
        payload: { cleanupOperationId: operationId, turnId: `workflow-cleanup-turn:${operationId}` },
      },
      acknowledgementPolicy: 'matching-receipt',
    }
    const { outbox } = makeOutbox({
      boundedConcurrency: 1,
      deliver: {
        async send() {
          return []
        },
        async sendBatch(records) {
          const record = records[0]
          if (!record) return []
          if (record.producerFamily === 'session-followup') {
            followupStarted()
            await followupGate
          }
          return records.map((entry) => [
            {
              type: entry.event.type,
              cleanupOperationId: entry.event.payload.cleanupOperationId,
              inputDeliveryId: entry.event.payload.inputDeliveryId,
              agentTurnId: entry.event.payload.turnId,
              agentSessionId: 'agent-session-1',
            } as {
              type: string
              cleanupOperationId?: string
              inputDeliveryId?: string
              agentTurnId?: string
              agentSessionId?: string
            },
          ])
        },
      },
    })
    await outbox.load()
    await outbox.enqueueBeforeExecution(cleanupBoundary)
    await outbox.enqueueBeforeExecution(followupInput)
    await started

    const wait = outbox.awaitCleanupPredecessorDelivery?.(waitTarget(2, operationId), {
      budgetMs: 1000,
      signal: new AbortController().signal,
    })
    if (!wait) throw new Error('production outbox must expose cleanup predecessor delivery wait')
    let settled = false
    wait.then(() => {
      settled = true
    })
    await flushMicrotasks()
    expect(outbox.snapshot().map((record) => record.id)).toEqual([followupInput.id])
    expect(settled).toBe(false)

    releaseFollowup()
    await expect(wait).resolves.toBeUndefined()
    expect(outbox.snapshot()).toEqual([])
  })

  it('does not block on an unrelated cleanup operation', async () => {
    const unrelated: RuntimeEventRecord = {
      ...workflowFact('workflow-cleanup:wf-1:task-other:work-other:1'),
      id: 'workflow-cleanup:wf-1:task-other:work-other:1',
      producerFamily: 'workflow-cleanup',
      event: { type: 'session.cleanup', payload: { cleanupOperationId: 'other-operation' } },
    }
    const { outbox } = makeOutbox({
      deliver: {
        async send() {
          return [{ type: 'session.cleanup' }]
        },
      },
    })
    await outbox.load()
    await outbox.enqueueBeforeExecution(unrelated)

    await expect(
      outbox.awaitCleanupPredecessorDelivery?.(waitTarget(2, 'workflow-cleanup:wf-1:task-1.1:work-1:1'), {
        budgetMs: 1000,
        signal: new AbortController().signal,
      }),
    ).resolves.toBeUndefined()
  })

  it('rejects with predecessor evidence when the budget expires and does not poll', async () => {
    const sendBatch = vi.fn(async () => {
      throw new Error('server unavailable')
    })
    const priorOperationId = 'workflow-cleanup:wf-1:task-1.1:work-1:1'
    const pendingFollowup: RuntimeEventRecord = {
      id: `${priorOperationId}:terminal`,
      producerFamily: 'session-followup',
      target: { kind: 'session', sessionId: 'agent-session-1' },
      runtimeSessionId: 'runtime-1',
      sessionTurnId: `workflow-cleanup-turn:${priorOperationId}`,
      work: null,
      event: { type: 'session.activity', payload: { cleanupOperationId: priorOperationId } },
      acknowledgementPolicy: 'matching-receipt',
    }
    const { outbox } = makeOutbox({ deliver: { send: async () => [], sendBatch }, retryDelayMs: 10_000 })
    await outbox.load()
    await outbox.enqueueProducedFact(pendingFollowup)
    await flushMicrotasks()
    const wait = outbox.awaitCleanupPredecessorDelivery?.(waitTarget(2, priorOperationId), {
      budgetMs: 100,
      signal: new AbortController().signal,
    })
    if (!wait) throw new Error('production outbox must expose cleanup predecessor delivery wait')

    const timeout = expect(wait).rejects.toMatchObject({
      name: 'CleanupPredecessorDeliveryWaitTimeoutError',
      projectId: 'proj-1',
      workflowRunId: 'wf-1',
      sessionName: 'build',
      cleanupAttempt: 2,
      precedingCleanupOperationId: priorOperationId,
      budgetMs: 100,
    } satisfies Partial<CleanupPredecessorDeliveryWaitTimeoutError>)
    await vi.advanceTimersByTimeAsync(99)
    expect(sendBatch).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1)
    await timeout
  })

  it('does not resolve after a settlement persistence failure', async () => {
    const fileSystem = new RecordingFileSystem()
    let release!: () => void
    let started!: () => void
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    const deliveryStarted = new Promise<void>((resolve) => {
      started = resolve
    })
    const { outbox } = makeOutbox({
      fileSystem,
      boundedConcurrency: 1,
      deliver: {
        async send() {
          return []
        },
        async sendBatch(records) {
          started()
          await gate
          return records.map((record) => [{ type: record.event.type }])
        },
      },
    })
    await outbox.load()
    await outbox.enqueueProducedFact(workflowFact('persist-failure-wait'))
    await deliveryStarted
    const wait = outbox.awaitCleanupPredecessorDelivery?.(waitTarget(1, null), {
      budgetMs: 1000,
      signal: new AbortController().signal,
    })
    if (!wait) throw new Error('production outbox must expose cleanup predecessor delivery wait')
    let settled = false
    wait.then(() => {
      settled = true
    })
    fileSystem.failNextWrite = () => new Error('disk full')
    release()
    await flushMicrotasks(8)
    expect(settled).toBe(false)
    expect(outbox.snapshot().map((record) => record.id)).toEqual(['persist-failure-wait'])
  })

  it('cancels promptly and leaves retained records unchanged', async () => {
    const { outbox } = makeOutbox({
      deliver: {
        send: async () => {
          throw new Error('unavailable')
        },
      },
    })
    await outbox.load()
    await outbox.enqueueProducedFact(workflowFact('cancel-me'))
    const controller = new AbortController()
    const wait = outbox.awaitCleanupPredecessorDelivery?.(waitTarget(1, null), {
      budgetMs: 1000,
      signal: controller.signal,
    })
    if (!wait) throw new Error('production outbox must expose cleanup predecessor delivery wait')
    controller.abort(new Error('caller cancelled'))
    await expect(wait).rejects.toThrow('caller cancelled')
    expect(outbox.snapshot().map((record) => record.id)).toEqual(['cancel-me'])
  })
})
