import { describe, expect, it, vi } from 'vitest'
import type { DispatchWorkItem } from '../src/core/types.js'
import { AgentJobExecutor } from '../src/runtime/agent-job-executor.js'
import type { ManagerExecutionBoundary } from '../src/runtime/manager-execution-boundary.js'
import type { OpenCodeRuntime, RuntimeTurnObserver, RuntimeTurnRequest } from '../src/runtime/opencode/index.js'
import type { ServerConnection } from '../src/server/connection.js'
import { withDefaultRunnerTestResources } from './support/test-resources.js'

describe('Manager AgentJob runtime binding', () => {
  it('publishes the isolated physical session before the Server attach is visible', async () => {
    await withDefaultRunnerTestResources(async () => {
      const order: string[] = []
      const isolatedRuntime = {
        ready: () => true,
        diagnostic: () => null,
        async runTurn(_request: RuntimeTurnRequest, _signal: AbortSignal, observer?: RuntimeTurnObserver) {
          await observer?.onSessionReady?.({ runtimeSessionId: 'isolated-session', workDir: '/work/manager' })
          return {
            ok: true as const,
            value: {
              facts: {
                finalAssistantText: 'done',
                runtimeSessionId: 'isolated-session',
                workDir: '/work/manager',
              },
              diagnostics: [],
            },
            diagnostics: [],
          }
        },
      } as unknown as OpenCodeRuntime
      const boundary = {
        openCodeRuntime: vi.fn(async () => isolatedRuntime),
        redact: (value: unknown) => value,
        hasExpired: () => false,
      } as unknown as ManagerExecutionBoundary
      const connection = {
        runnerId: 'runner-1',
        getAgentSession: vi.fn(async () => ({ runtime: 'opencode', runtimeSessionId: null })),
        openAgentSession: vi.fn(async () => {
          order.push('open')
        }),
        attachAgentSession: vi.fn(async () => {
          order.push('attach')
        }),
        agentSessionRuntimeEvents: vi.fn(async () => undefined),
      } as unknown as ServerConnection
      const onManagerRuntimeSessionReady = vi.fn(async (binding) => {
        order.push('bind')
        expect(binding).toEqual({
          boundary,
          handle: { kind: 'opencode', runtime: isolatedRuntime },
          sessionId: 'session-1',
          runtimeSessionId: 'isolated-session',
          workDir: '/work/manager',
        })
      })
      const executor = new AgentJobExecutor(
        connection,
        { openCode: { ready: () => true } as OpenCodeRuntime, pi: null },
        '/work/manager',
        undefined,
        null,
        { onManagerRuntimeSessionReady },
      )
      const work: DispatchWorkItem = {
        workflowRunId: '',
        workId: 'job-1',
        workType: 'task',
        ownerKind: 'agent-job',
        agentJobId: 'job-1',
        agentSessionId: 'session-1',
        projectId: 'project-1',
        initialInputId: 'input-1',
        initialTurnId: 'turn-1',
        with: { prompt: 'run', runtime: 'opencode' },
        variables: { workspace: { path: '/work/manager' } },
      }

      await expect(executor.execute(work, new AbortController().signal, boundary)).resolves.toMatchObject({
        status: 'completed',
        agentBinding: { runtimeSessionId: 'isolated-session' },
      })
      expect(order).toEqual(['bind', 'open', 'attach'])
      expect(boundary.openCodeRuntime).toHaveBeenCalledOnce()
      expect(onManagerRuntimeSessionReady).toHaveBeenCalledOnce()
    })
  })
})
