import { afterEach, describe, expect, it, vi } from 'vitest'
import { builtInActions } from '../src/actions/built-ins.js'
import { capabilitySet } from '../src/actions/host.js'
import { normalizeActionResult } from '../src/actions/result-validation.js'
import { piAction } from '../src/actions/pi.js'
import {
  workflowCleanupInputDeliveryId,
  workflowCleanupOperationId,
  workflowCleanupTurnId,
} from '../src/actions/workflow-agent-session-reporter.js'
import type { DispatchWorkItem } from '../src/core/types.js'
import { buildActionHost, type ExecutorCapabilityDeps } from '../src/runtime/executor-capabilities.js'
import { type AgentSessionRuntimeEventOutbox, type RuntimeEventRecord } from '../src/server/runtime-event-outbox.js'
import { SkillResolver } from '../src/runtime/skill-resolver.js'
import { RuntimeTurnRegistry } from '../src/runtime/runtime-turn-registry.js'
import { workKey } from '../src/runtime/work-result-journal.js'
import { createTerminalRecoveryReceipt } from '../src/runtime/recovery-receipt.js'
import { flushMicrotasks, makeOutbox, workflowFact } from './support/runtime-event-outbox-fixture.js'

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  const promise = new Promise<T>((res) => {
    resolve = res
  })
  return { promise, resolve }
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

function opencodeRuntime(order?: string[]): any {
  return {
    ready: () => true,
    diagnostic: () => null,
    createSession: vi.fn(),
    resolveSession: vi.fn(async () => ({ ok: true, value: { activeTurn: false }, diagnostics: [] })),
    runTurn: vi.fn(async () => {
      order?.push('run')
      return {
        ok: true as const,
        value: {
          facts: { runtimeSessionId: 'runtime-1', workDir: '/workspace', finalAssistantText: 'done' },
          diagnostics: [],
        },
        diagnostics: [],
      }
    }),
  } as never
}

function opencodeDeps(
  connection: unknown,
  runtime: unknown,
  eventOutbox: AgentSessionRuntimeEventOutbox,
  budgetMs = 60_000,
  runtimeTurnRegistry?: RuntimeTurnRegistry,
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
    runtimeTurnRegistry,
    cleanupTerminalFactDeliveryBudgetMs: budgetMs,
  }
}

function admissionHost(
  eventOutbox: AgentSessionRuntimeEventOutbox,
  open: ReturnType<typeof vi.fn>,
  cleanupAttempt: number | undefined,
  item = work(),
  runtime = opencodeRuntime(),
  budgetMs = 60_000,
  runtimeTurnRegistry?: RuntimeTurnRegistry,
) {
  return buildActionHost(
    opencodeDeps(openConnection(open), runtime, eventOutbox, budgetMs, runtimeTurnRegistry),
    item,
    '/workspace',
    new AbortController().signal,
    null as never,
    new Set(['agent-turn']),
    cleanupAttempt,
  )
}

function piRuntime(order?: string[]): any {
  return {
    ready: () => true,
    diagnostic: () => null,
    createSession: vi.fn(),
    runTurn: vi.fn(async () => {
      order?.push('run')
      return {
        ok: true as const,
        value: {
          facts: { finalAssistantText: 'done', runtimeSessionId: 'pi-runtime-1', workDir: '/workspace' },
        },
        diagnostics: [],
      }
    }),
  } as never
}

function runPi(options: {
  outbox: AgentSessionRuntimeEventOutbox
  open: ReturnType<typeof vi.fn>
  cleanupAttempt: number
  runtime?: ReturnType<typeof piRuntime>
  budgetMs?: number
  sessionName?: string
  runtimeTurnRegistry?: RuntimeTurnRegistry
}) {
  return piAction({
    workflowRunId: 'workflow-1',
    workId: 'work-1',
    taskRunId: 'task-1',
    workType: 'task',
    workDir: '/workspace',
    signal: new AbortController().signal,
    projectId: 'project-1',
    with: { prompt: 'cleanup', session: options.sessionName ?? 'work-1' },
    piRuntime: options.runtime ?? piRuntime(),
    serverConnection: openConnection(options.open),
    runtimeEventOutbox: options.outbox,
    runtimeEventRecordId: (() => {
      let n = 0
      return () => `record-${++n}`
    })(),
    runtimeTurnRegistry: options.runtimeTurnRegistry,
    runnerId: 'runner-1',
    cleanupAttempt: options.cleanupAttempt,
    cleanupTerminalFactDeliveryBudgetMs: options.budgetMs ?? 60_000,
    preparedPrompt: 'cleanup',
    preparedOptions: {},
  })
}

function originalTerminalRecord(): RuntimeEventRecord {
  return workflowFact('original-terminal', {
    target: { kind: 'workflow', projectId: 'project-1', workflowRunId: 'workflow-1', sessionName: 'work-1' },
    work: {
      workId: 'work-1',
      taskRunId: 'task-1',
      runnerId: 'runner-1',
      agentSessionId: 'agent-session-1',
      inputDeliveryId: 'original-input',
      agentTurnId: 'original-turn',
      workType: 'task',
      stage: 'build',
    },
    event: { type: 'session.activity', payload: { activity: 'idle' } },
  })
}

function cleanupBoundary(operationId: string, runtime: 'opencode' | 'pi'): RuntimeEventRecord {
  const turnId = workflowCleanupTurnId(operationId)
  return {
    id: operationId,
    producerFamily: 'workflow-cleanup',
    target: { kind: 'workflow', projectId: 'project-1', workflowRunId: 'workflow-1', sessionName: 'work-1' },
    runtime,
    runtimeSessionId: runtime === 'pi' ? 'pi-runtime-1' : 'runtime-1',
    work: {
      workId: 'work-1',
      taskRunId: 'task-1',
      runnerId: 'runner-1',
      agentSessionId: 'agent-session-1',
      inputDeliveryId: workflowCleanupInputDeliveryId(operationId),
      agentTurnId: null,
      workType: 'task',
      stage: 'build',
    },
    event: {
      type: 'session.cleanup',
      payload: {
        text: 'prior cleanup',
        cleanupOperationId: operationId,
        inputDeliveryId: workflowCleanupInputDeliveryId(operationId),
        turnId,
        attempt: 1,
      },
    },
    acknowledgementPolicy: 'matching-receipt',
  }
}

function cleanupTerminalFollowup(operationId: string, runtime: 'opencode' | 'pi'): RuntimeEventRecord {
  return {
    id: `${operationId}:terminal`,
    producerFamily: 'session-followup',
    target: { kind: 'session', sessionId: 'agent-session-1' },
    runtimeSessionId: runtime === 'pi' ? 'pi-runtime-1' : 'runtime-1',
    sessionTurnId: workflowCleanupTurnId(operationId),
    work: null,
    event: {
      type: 'session.activity',
      payload: { activity: 'idle', cleanupOperationId: operationId, turnId: workflowCleanupTurnId(operationId) },
    },
    acknowledgementPolicy: 'matching-receipt',
  }
}

function receiptFor(record: RuntimeEventRecord) {
  return {
    type: record.event.type,
    cleanupOperationId: record.event.payload.cleanupOperationId as string | undefined,
    inputDeliveryId: record.event.payload.inputDeliveryId as string | undefined,
    agentTurnId: record.event.payload.turnId as string | undefined,
    agentSessionId: record.work?.agentSessionId ?? 'agent-session-1',
  }
}

function productionOutbox(
  sendBatch: (records: readonly RuntimeEventRecord[]) => Promise<ReturnType<typeof receiptFor>[][]>,
  retryDelayMs = 10_000,
) {
  return makeOutbox({
    boundedConcurrency: 4,
    retryDelayMs,
    deliver: {
      async send() {
        return []
      },
      sendBatch,
    },
  }).outbox
}

async function waitForRecordRemoval(outbox: AgentSessionRuntimeEventOutbox, recordId: string) {
  for (let i = 0; i < 20 && outbox.snapshot().some((record) => record.id === recordId); i += 1) {
    await flushMicrotasks()
  }
  expect(outbox.snapshot().some((record) => record.id === recordId)).toBe(false)
}

async function seedSettledBoundaryWithRetainedTerminal(runtime: 'opencode' | 'pi', order: string[]) {
  const operationId = workflowCleanupOperationId('workflow-1', 'task-1', 'work-1', 1)
  const gate = deferred<void>()
  const followupStarted = deferred<void>()
  const outbox = productionOutbox(async (records) => {
    const record = records[0]
    if (record?.id === `${operationId}:terminal`) {
      order.push('predecessor-start')
      followupStarted.resolve()
      await gate.promise
      order.push('predecessor-release')
    }
    if (record?.producerFamily === 'workflow-cleanup') order.push('cleanup-boundary')
    if (record?.producerFamily === 'session-followup' && record.event.type === 'session.input') {
      order.push('cleanup-input')
    }
    return records.map((entry) => [receiptFor(entry)])
  })
  await outbox.load()
  await outbox.enqueueBeforeExecution(cleanupBoundary(operationId, runtime))
  await waitForRecordRemoval(outbox, operationId)
  await outbox.enqueueProducedFact(cleanupTerminalFollowup(operationId, runtime))
  await followupStarted.promise
  expect(outbox.snapshot().map((record) => record.id)).toEqual([`${operationId}:terminal`])
  return { outbox, gate, operationId }
}

afterEach(() => {
  vi.useRealTimers()
})

describe('cleanup turn admission', () => {
  it('preserves the original work binding and terminal receipt identity across OpenCode and Pi cleanup turns', async () => {
    const registry = new RuntimeTurnRegistry()
    const original = {
      agentSessionId: 'agent-session-1',
      agentTurnId: 'original-turn',
      runtime: 'opencode' as const,
      runtimeSessionId: 'runtime-1',
      workDir: '/workspace',
    }
    const originalWork = work({ projectId: null })
    const key = workKey(originalWork)
    registry.register(key, original)

    const openCodeResult = await admissionHost(
      makeOutbox({}).outbox,
      vi.fn(),
      1,
      originalWork,
      opencodeRuntime(),
      60_000,
      registry,
    ).agent!.turn({ prompt: 'cleanup', session: 'work-1' })
    expect(openCodeResult.error).toBeUndefined()
    expect(registry.get(key)).toEqual(original)

    const piOutbox = productionOutbox(async (records) => records.map((record) => [receiptFor(record)]))
    await piOutbox.load()
    const piResult = await runPi({
      outbox: piOutbox,
      open: vi.fn(async () => ({
        sessionId: 'agent-session-1',
        runtimeSessionId: 'pi-runtime-1',
        runtime: 'pi',
        workDir: '/workspace',
      })),
      cleanupAttempt: 1,
      runtime: piRuntime(),
      runtimeTurnRegistry: registry,
    })
    expect(piResult.error).toBeUndefined()
    expect(registry.get(key)).toEqual(original)

    const receipt = createTerminalRecoveryReceipt(
      originalWork,
      registry.get(key)!,
      'runner-1',
      { status: 'completed', output: { ok: true } },
      'receipt-1',
    )
    expect(receipt?.agentTurnId).toBe('original-turn')
    await piOutbox.stop()
  })

  it('holds OpenCode attempt 1 on the production outbox before open and cleanup submission', async () => {
    const gate = deferred<void>()
    const started = deferred<void>()
    const order: string[] = []
    const outbox = productionOutbox(async (records) => {
      if (records[0]?.id === 'original-terminal') {
        order.push('predecessor-start')
        started.resolve()
        await gate.promise
        order.push('predecessor-release')
      }
      if (records[0]?.producerFamily === 'workflow-cleanup') order.push('cleanup-boundary')
      if (records[0]?.producerFamily === 'session-followup' && records[0].event.type === 'session.input') {
        order.push('cleanup-input')
      }
      return records.map((record) => [receiptFor(record)])
    })
    await outbox.load()
    await outbox.enqueueProducedFact(originalTerminalRecord())
    await started.promise
    const open = vi.fn(async () => {
      order.push('open')
      return {
        sessionId: 'agent-session-1',
        runtimeSessionId: 'runtime-1',
        workDir: '/workspace',
        status: 'active',
      }
    })
    const runtime = opencodeRuntime(order)

    const resultPromise = admissionHost(outbox, open, 1, work(), runtime).agent!.turn({
      prompt: 'cleanup',
      session: 'work-1',
    })
    await flushMicrotasks()
    expect(open).not.toHaveBeenCalled()
    expect(runtime.runTurn).not.toHaveBeenCalled()

    gate.resolve()
    const result = await resultPromise
    expect(result.error).toBeUndefined()
    expect(order.indexOf('predecessor-release')).toBeLessThan(order.indexOf('open'))
    expect(order.indexOf('open')).toBeLessThan(order.indexOf('cleanup-boundary'))
    expect(order.indexOf('cleanup-input')).toBeLessThan(order.indexOf('run'))
    await outbox.stop()
  })

  it('holds Pi attempt 1 on the production outbox before open and cleanup submission', async () => {
    const gate = deferred<void>()
    const started = deferred<void>()
    const order: string[] = []
    const outbox = productionOutbox(async (records) => {
      if (records[0]?.id === 'original-terminal') {
        order.push('predecessor-start')
        started.resolve()
        await gate.promise
        order.push('predecessor-release')
      }
      if (records[0]?.producerFamily === 'workflow-cleanup') order.push('cleanup-boundary')
      if (records[0]?.producerFamily === 'session-followup' && records[0].event.type === 'session.input') {
        order.push('cleanup-input')
      }
      return records.map((record) => [receiptFor(record)])
    })
    await outbox.load()
    await outbox.enqueueProducedFact(originalTerminalRecord())
    await started.promise
    const open = vi.fn(async () => {
      order.push('open')
      return {
        sessionId: 'agent-session-1',
        runtimeSessionId: 'pi-runtime-1',
        runtime: 'pi',
        workDir: '/workspace',
      }
    })
    const runtime = piRuntime(order)

    const resultPromise = runPi({ outbox, open, cleanupAttempt: 1, runtime })
    await flushMicrotasks()
    expect(open).not.toHaveBeenCalled()
    expect(runtime.runTurn).not.toHaveBeenCalled()

    gate.resolve()
    const result = await resultPromise
    expect(result.error, JSON.stringify({ order, snapshot: outbox.snapshot() })).toBeUndefined()
    expect(order.indexOf('predecessor-release')).toBeLessThan(order.indexOf('open'))
    expect(order.indexOf('open')).toBeLessThan(order.indexOf('cleanup-boundary'))
    expect(order.indexOf('cleanup-input')).toBeLessThan(order.indexOf('run'))
    await outbox.stop()
  })

  it('keeps OpenCode attempt 2 closed after the prior Workflow boundary settled while its terminal fact remains retained', async () => {
    const order: string[] = []
    const { outbox, gate, operationId } = await seedSettledBoundaryWithRetainedTerminal('opencode', order)
    const open = vi.fn(async () => {
      order.push('open')
      return {
        sessionId: 'agent-session-1',
        runtimeSessionId: 'runtime-1',
        workDir: '/workspace',
        status: 'unknown',
      }
    })
    const runtime = opencodeRuntime(order)

    const resultPromise = admissionHost(outbox, open, 2, work(), runtime).agent!.turn({
      prompt: 'cleanup',
      session: 'work-1',
    })
    await flushMicrotasks()
    expect(open).not.toHaveBeenCalled()
    expect(outbox.snapshot().map((record) => record.id)).toEqual([`${operationId}:terminal`])

    gate.resolve()
    const result = await resultPromise
    expect(result.error).toBeUndefined()
    expect(order.indexOf('predecessor-release')).toBeLessThan(order.indexOf('open'))
    expect(order.indexOf('cleanup-input')).toBeLessThan(order.indexOf('run'))
    await outbox.stop()
  })

  it('keeps Pi attempt 2 closed after the prior Workflow boundary settled while its terminal fact remains retained', async () => {
    const order: string[] = []
    const { outbox, gate, operationId } = await seedSettledBoundaryWithRetainedTerminal('pi', order)
    const open = vi.fn(async () => {
      order.push('open')
      return {
        sessionId: 'agent-session-1',
        runtimeSessionId: 'pi-runtime-1',
        runtime: 'pi',
        workDir: '/workspace',
      }
    })
    const runtime = piRuntime(order)

    const resultPromise = runPi({ outbox, open, cleanupAttempt: 2, runtime })
    await flushMicrotasks()
    expect(open).not.toHaveBeenCalled()
    expect(outbox.snapshot().map((record) => record.id)).toEqual([`${operationId}:terminal`])

    gate.resolve()
    const result = await resultPromise
    expect(result.error, JSON.stringify({ order, snapshot: outbox.snapshot() })).toBeUndefined()
    expect(order.indexOf('predecessor-release')).toBeLessThan(order.indexOf('open'))
    expect(order.indexOf('cleanup-input')).toBeLessThan(order.indexOf('run'))
    await outbox.stop()
  })

  it('admits both runtimes immediately through the production outbox when predecessor facts are already delivered', async () => {
    const outbox = productionOutbox(async (records) => records.map((record) => [receiptFor(record)]))
    await outbox.load()
    const openCodeOpen = vi.fn(async () => ({
      sessionId: 'agent-session-1',
      runtimeSessionId: 'runtime-1',
      workDir: '/workspace',
      status: 'active',
    }))
    const piOpen = vi.fn(async () => ({
      sessionId: 'agent-session-1',
      runtimeSessionId: 'pi-runtime-1',
      runtime: 'pi',
      workDir: '/workspace',
    }))

    const openCodeResult = await admissionHost(outbox, openCodeOpen, 1).agent!.turn({
      prompt: 'cleanup',
      session: 'work-1',
    })
    const piResult = await runPi({ outbox, open: piOpen, cleanupAttempt: 1 })

    expect(openCodeResult.error).toBeUndefined()
    expect(piResult.error).toBeUndefined()
    expect(openCodeOpen).toHaveBeenCalledTimes(1)
    expect(piOpen).toHaveBeenCalledTimes(1)
    await outbox.stop()
  })

  it('maps a production OpenCode wait expiry to structured cleanup timeout evidence', async () => {
    vi.useFakeTimers()
    const outbox = productionOutbox(async () => {
      throw new Error('server unavailable')
    })
    await outbox.load()
    await outbox.enqueueProducedFact(originalTerminalRecord())
    await flushMicrotasks()
    const open = vi.fn()

    const resultPromise = admissionHost(outbox, open, 1, work(), opencodeRuntime(), 25).agent!.turn({
      prompt: 'cleanup',
      session: 'work-1',
    })
    await vi.advanceTimersByTimeAsync(25)
    const result = await resultPromise

    expect(result).toMatchObject({ error: { code: 'session-delivery-wait-timeout' } })
    expect(result.error?.message).toContain('project-1/workflow-1/work-1')
    expect(result.error?.message).toContain('work item work-1')
    expect(result.error?.message).toContain('25ms')
    expect(open).not.toHaveBeenCalled()
    await outbox.stop()
  })

  it('maps a production Pi wait expiry to structured cleanup timeout evidence', async () => {
    vi.useFakeTimers()
    const outbox = productionOutbox(async () => {
      throw new Error('server unavailable')
    })
    await outbox.load()
    await outbox.enqueueProducedFact(originalTerminalRecord())
    await flushMicrotasks()
    const open = vi.fn()

    const resultPromise = runPi({ outbox, open, cleanupAttempt: 1, budgetMs: 31 })
    await vi.advanceTimersByTimeAsync(31)
    const result = await resultPromise

    expect(result).toMatchObject({ error: { code: 'session-delivery-wait-timeout' } })
    expect(result.error?.message).toContain('project-1/workflow-1/work-1')
    expect(result.error?.message).toContain('work item work-1')
    expect(result.error?.message).toContain('31ms')
    expect(open).not.toHaveBeenCalled()
    await outbox.stop()
  })

  it('keeps an unbindable cross-attempt OpenCode cleanup fail-closed', async () => {
    const outbox = productionOutbox(async (records) => records.map((record) => [receiptFor(record)]))
    await outbox.load()
    const open = vi.fn(async () => ({
      sessionId: 'agent-session-1',
      runtimeSessionId: 'runtime-1',
      workDir: '/workspace',
      status: 'active',
    }))

    const result = await admissionHost(outbox, open, 2, work({ taskRunId: null })).agent!.turn({
      prompt: 'cleanup',
      session: 'work-1',
    })

    expect(result).toMatchObject({ error: { code: 'session-binding-failed' } })
    expect(result.error?.message).toContain('retry is fail-closed')
    expect(outbox.snapshot()).toEqual([])
    await outbox.stop()
  })

  it('declares and preserves the cleanup delivery timeout code in both runtime manifests', () => {
    for (const name of ['mohist/opencode', 'mohist/pi']) {
      const action = builtInActions.find((candidate) => candidate.manifest.name === name)
      expect(action, `${name} built-in action`).toBeDefined()
      expect(action!.manifest.errors.map((error) => error.code)).toContain('session-delivery-wait-timeout')
      expect(
        normalizeActionResult(
          { error: { code: 'session-delivery-wait-timeout', message: 'timed out' } },
          action!.manifest,
          capabilitySet(action!.manifest),
        ),
      ).toEqual({
        kind: 'error',
        error: { code: 'session-delivery-wait-timeout', message: 'timed out' },
      })
    }
  })
})
