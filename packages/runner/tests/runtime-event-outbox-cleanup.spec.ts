import { describe, expect, it } from 'vitest'
import { AlreadyConsumedRuntimeEventError, type RuntimeEventRecord } from '../src/server/runtime-event-outbox.js'
import { flushMicrotasks, makeOutbox, workflowFact } from './support/runtime-event-outbox-fixture.js'

describe('AgentSessionRuntimeEventOutbox - workflow cleanup FIFO', () => {
  it('settles a cleanup record as already consumed after two consecutive empty receipts', async () => {
    const cleanupOperationId = 'workflow-cleanup:wf-1:task-1.1:work-1:1'
    const { outbox } = makeOutbox({
      deliver: {
        async send() {
          return []
        },
      },
    })
    await outbox.load()
    const record: RuntimeEventRecord = {
      id: cleanupOperationId,
      producerFamily: 'workflow-cleanup',
      target: { kind: 'workflow', projectId: 'proj-1', workflowRunId: 'wf-1', sessionName: 'build' },
      runtime: 'pi',
      runtimeSessionId: 'runtime-1',
      work: {
        workId: 'work-1',
        taskRunId: 'task-1.1',
        runnerId: 'runner-1',
        agentSessionId: 'agent-session-1',
        inputDeliveryId: `workflow-cleanup-input:${cleanupOperationId}`,
        agentTurnId: null,
        workType: 'task',
        stage: 'build',
      },
      event: {
        type: 'session.cleanup',
        payload: {
          text: 'clean the worktree',
          cleanupOperationId,
          inputDeliveryId: `workflow-cleanup-input:${cleanupOperationId}`,
          turnId: `workflow-cleanup-turn:${cleanupOperationId}`,
        },
      },
      acknowledgementPolicy: 'matching-receipt',
    }
    await outbox.enqueueBeforeExecution(record)
    const waiter = outbox.awaitInputReceipt?.(cleanupOperationId)
    if (!waiter) throw new Error('outbox must support cleanup receipts')

    await outbox.kick()
    expect(outbox.snapshot()).toHaveLength(1)
    await outbox.kick()

    await expect(waiter).rejects.toBeInstanceOf(AlreadyConsumedRuntimeEventError)
    await expect(outbox.awaitInputReceipt?.(cleanupOperationId)).rejects.toMatchObject({
      classification: 'already-consumed',
      recordId: cleanupOperationId,
    })
    expect(outbox.snapshot()).toEqual([])
  })

  it('keeps cleanup admission and the next input behind the original terminal receipt', async () => {
    const batches: string[][] = []
    let startTerminal!: () => void
    let releaseTerminal!: () => void
    const terminalStarted = new Promise<void>((resolve) => {
      startTerminal = resolve
    })
    const terminalGate = new Promise<void>((resolve) => {
      releaseTerminal = resolve
    })
    const cleanupOperationId = 'workflow-cleanup:wf-1:task-1.1:work-1:1'

    const { outbox } = makeOutbox({
      boundedConcurrency: 1,
      deliver: {
        async send() {
          throw new Error('batched delivery expected')
        },
        async sendBatch(records) {
          const ids = records.map((record) => record.id)
          batches.push(ids)
          const head = records[0]
          if (!head) return []
          if (head.id === 'terminal-1') {
            startTerminal()
            await terminalGate
            return [[{ type: 'session.activity' }]]
          }
          if (head.id === cleanupOperationId) {
            return [
              [
                {
                  type: 'session.cleanup',
                  cleanupOperationId,
                  inputDeliveryId: 'workflow-cleanup-input:workflow-cleanup:wf-1:task-1.1:work-1:1',
                  agentTurnId: 'workflow-cleanup-turn:workflow-cleanup:wf-1:task-1.1:work-1:1',
                  agentSessionId: 'agent-session-1',
                },
              ],
            ]
          }
          return [
            [
              {
                type: 'session.input',
                inputDeliveryId: head.id,
                agentTurnId: 'turn-next',
                agentSessionId: 'agent-session-1',
              },
            ],
          ]
        },
      },
    })
    await outbox.load()

    const template = workflowFact('template')
    await outbox.enqueueProducedFact({
      ...template,
      id: 'terminal-1',
      event: { type: 'session.activity', payload: { activity: 'idle' } },
    })
    const drain = outbox.kick()
    await terminalStarted

    const cleanup: RuntimeEventRecord = {
      ...template,
      id: cleanupOperationId,
      producerFamily: 'workflow-cleanup',
      work: {
        ...template.work!,
        inputDeliveryId: 'workflow-cleanup-input:workflow-cleanup:wf-1:task-1.1:work-1:1',
        agentTurnId: null,
      },
      event: {
        type: 'session.cleanup',
        payload: {
          text: 'clean the worktree',
          cleanupOperationId,
          inputDeliveryId: 'workflow-cleanup-input:workflow-cleanup:wf-1:task-1.1:work-1:1',
          turnId: 'workflow-cleanup-turn:workflow-cleanup:wf-1:task-1.1:work-1:1',
        },
      },
    }
    await outbox.enqueueBeforeExecution(cleanup)
    await outbox.enqueueBeforeExecution({
      ...template,
      id: 'next-input',
      event: { type: 'session.input', payload: { text: 'next turn' } },
      work: { ...template.work!, inputDeliveryId: 'next-input', agentTurnId: null },
    })

    const cleanupReceipt = outbox.awaitInputReceipt?.(cleanupOperationId)
    if (!cleanupReceipt) throw new Error('outbox must support cleanup receipts')
    await flushMicrotasks()
    expect(batches).toEqual([['terminal-1']])
    expect(outbox.snapshot().map((record) => record.id)).toEqual(['terminal-1', cleanupOperationId, 'next-input'])

    releaseTerminal()
    await expect(cleanupReceipt).resolves.toMatchObject({
      type: 'session.cleanup',
      agentTurnId: 'workflow-cleanup-turn:workflow-cleanup:wf-1:task-1.1:work-1:1',
    })
    await drain

    expect(batches).toEqual([['terminal-1'], [cleanupOperationId], ['next-input']])
    expect(outbox.snapshot()).toEqual([])
    await outbox.stop()
  })
})
