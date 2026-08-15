import { describe, expect, it, vi } from 'vitest'
import type { ServerConnection } from '../src/server/connection.js'
import { createServerRuntimeEventDelivery } from '../src/server/runtime-event-delivery.js'
import type { RuntimeEventRecord } from '../src/server/runtime-event-outbox.js'

describe('createServerRuntimeEventDelivery - workflow cleanup', () => {
  it('uses the Server-owned cleanup admission route with stable identity', async () => {
    const cleanupTurn = vi.fn(
      async (_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown) => {
        expect(body).toEqual({
          cleanupOperationId: 'workflow-cleanup:wf-1:task-1.1:work-1:1',
          prompt: 'clean the worktree',
          taskRunId: 'task-1.1',
          workId: 'work-1',
          agentSessionId: 'agent-session-1',
          runtime: 'pi',
          runtimeSessionId: 'runtime-1',
        })
        return {
          cleanupOperationId: 'workflow-cleanup:wf-1:task-1.1:work-1:1',
          inputDeliveryId: 'workflow-cleanup-input:workflow-cleanup:wf-1:task-1.1:work-1:1',
          agentTurnId: 'workflow-cleanup-turn:workflow-cleanup:wf-1:task-1.1:work-1:1',
          agentSessionId: 'agent-session-1',
        }
      },
    )
    const delivery = createServerRuntimeEventDelivery({
      connection: { workflowAgentSessionCleanupTurn: cleanupTurn } as unknown as ServerConnection,
    })
    const record: RuntimeEventRecord = {
      id: 'workflow-cleanup:wf-1:task-1.1:work-1:1',
      producerFamily: 'workflow-cleanup',
      target: { kind: 'workflow', projectId: 'proj-1', workflowRunId: 'wf-1', sessionName: 'build' },
      runtime: 'pi',
      runtimeSessionId: 'runtime-1',
      work: {
        workId: 'work-1',
        taskRunId: 'task-1.1',
        runnerId: 'runner-1',
        agentSessionId: 'agent-session-1',
        inputDeliveryId: 'workflow-cleanup-input:workflow-cleanup:wf-1:task-1.1:work-1:1',
        agentTurnId: null,
        workType: 'task',
        stage: 'build',
      },
      event: {
        type: 'session.cleanup',
        payload: {
          text: 'clean the worktree',
          cleanupOperationId: 'workflow-cleanup:wf-1:task-1.1:work-1:1',
        },
      },
      acknowledgementPolicy: 'matching-receipt',
    }

    await expect(delivery.send(record, new AbortController().signal)).resolves.toEqual([
      {
        type: 'session.cleanup',
        inputDeliveryId: 'workflow-cleanup-input:workflow-cleanup:wf-1:task-1.1:work-1:1',
        agentTurnId: 'workflow-cleanup-turn:workflow-cleanup:wf-1:task-1.1:work-1:1',
        agentSessionId: 'agent-session-1',
      },
    ])
    expect(cleanupTurn).toHaveBeenCalledTimes(1)
  })
})
