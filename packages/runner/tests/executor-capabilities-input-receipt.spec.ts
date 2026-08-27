import { describe, expect, it, vi } from 'vitest'
import { buildActionHost, type ExecutorCapabilityDeps } from '../src/runtime/executor-capabilities.js'
import type { ActionHost } from '../src/actions/host.js'
import type { DispatchWorkItem } from '../src/core/types.js'
import type { OpenCodeRuntime } from '../src/runtime/opencode/index.js'
import {
  InputReceiptWaitCancelledError,
  InputReceiptWaitTimeoutError,
  type AgentSessionRuntimeEventQueue,
  type RuntimeEventInputReceiptWaitOptions,
  type RuntimeEventRecord,
} from '../src/server/runtime-event-queue.js'
import type { ServerConnection } from '../src/server/connection.js'
import type { SkillResolver } from '../src/runtime/skill-resolver.js'

interface ReceiptWaitCapture {
  recordId: string
  options?: RuntimeEventInputReceiptWaitOptions
}

function makeWork(overrides: Partial<DispatchWorkItem> = {}): DispatchWorkItem {
  return {
    workflowRunId: 'wf-receipt-1',
    workId: 'work-receipt-1',
    actionAttemptId: 'task-receipt-1',
    workType: 'task',
    stage: 'build',
    title: 'Receipt-bound turn',
    uses: 'mohist/opencode',
    with: { prompt: 'do the work' },
    variables: {},
    projectId: 'project-receipt-1',
    ...overrides,
  }
}

function makeRuntime() {
  const runTurn = vi.fn(async () => ({
    ok: true as const,
    value: {
      facts: { runtimeSessionId: 'runtime-receipt-1', finalAssistantText: 'done' },
      diagnostics: [],
    },
    diagnostics: [],
  }))
  return {
    runtime: {
      ready: () => true,
      diagnostic: () => null,
      runTurn,
    } as unknown as OpenCodeRuntime,
    runTurn,
  }
}

function makeQueue(failure: (recordId: string) => Error): {
  queue: AgentSessionRuntimeEventQueue
  records: RuntimeEventRecord[]
  waits: ReceiptWaitCapture[]
} {
  const records: RuntimeEventRecord[] = []
  const waits: ReceiptWaitCapture[] = []
  const queue: AgentSessionRuntimeEventQueue = {
    ready: () => true,
    async load() {},
    async recover() {},
    async enqueueBeforeExecution(record) {
      records.push(record)
    },
    async awaitInputReceipt(recordId, options) {
      waits.push({ recordId, options })
      throw failure(recordId)
    },
    async enqueueProducedFact() {},
    async enqueueProducedFactBatch() {},
    async kick() {},
    async stop() {},
    snapshot() {
      return [...records]
    },
  }
  return { queue, records, waits }
}

function makeDeps(runtime: OpenCodeRuntime, queue: AgentSessionRuntimeEventQueue): ExecutorCapabilityDeps {
  const skillResolver = {
    resolve: vi.fn(async () => ({ ok: true as const, skills: [] })),
  } as unknown as SkillResolver
  const connection = {
    runnerId: 'runner-receipt-1',
    async openWorkflowAgentSession() {
      return {
        sessionId: 'agent-session-receipt-1',
        runtimeSessionId: 'runtime-receipt-1',
        workDir: '/tmp/work',
      }
    },
  } as unknown as ServerConnection
  let sequence = 0
  return {
    connection,
    skillResolver,
    piRuntime: null,
    openCodeRuntime: runtime,
    agentSessionRuntimeEventQueue: queue,
    runtimeEventRecordId: () => `receipt-record-${++sequence}`,
  }
}

function makeHost(
  runtime: OpenCodeRuntime,
  queue: AgentSessionRuntimeEventQueue,
  work: DispatchWorkItem,
  signal: AbortSignal,
): ActionHost {
  return buildActionHost(
    makeDeps(runtime, queue),
    work,
    '/tmp/work',
    signal,
    { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() } as never,
    new Set(['agent-turn'] as const),
  )
}

const typedWaitFailures = [
  [
    'timeout',
    (recordId: string) =>
      new InputReceiptWaitTimeoutError(
        recordId,
        { attempts: 2, retries: 1, lastReason: 'retryable: temporarily unavailable' },
        321,
        321,
      ),
  ],
  ['cancellation', (recordId: string) => new InputReceiptWaitCancelledError(recordId, new Error('task stopped'))],
] as const

describe('buildActionHost OpenCode receipt-wait projection', () => {
  it.each(typedWaitFailures)(
    'passes the effective deadline and task signal through for a typed %s without invoking OpenCode',
    async (_kind, failure) => {
      const { runtime, runTurn } = makeRuntime()
      const { queue, records, waits } = makeQueue(failure)
      const controller = new AbortController()
      const host = makeHost(runtime, queue, makeWork(), controller.signal)

      const result = await host.agent!.turn({ prompt: 'submit only after receipt', deadlineMs: 321 })

      expect(result.error).toMatchObject({ code: 'session-reporting-failed' })
      expect(result.error?.message).toContain('session.input')
      expect(runTurn).not.toHaveBeenCalled()
      expect(records).toHaveLength(1)
      expect(waits).toHaveLength(1)
      expect(waits[0]?.recordId).toBe(records[0]?.id)
      expect(waits[0]?.options).toEqual({ budgetMs: 321, signal: controller.signal })
      expect(result.turnFact).toMatchObject({
        finalAssistantText: null,
        agentBinding: {
          agentSessionId: 'agent-session-receipt-1',
          agentTurnId: null,
          runtime: 'opencode',
          runtimeSessionId: 'runtime-receipt-1',
        },
      })
    },
  )
})
