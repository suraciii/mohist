import { afterEach, describe, expect, it, vi } from 'vitest'
import { WorkflowAgentSessionReporter } from '../src/actions/workflow-agent-session-reporter.js'
import {
  AlreadyConsumedRuntimeEventError,
  type AgentSessionRuntimeEventQueue,
  type RuntimeEventInputReceiptWaitOptions,
  type RuntimeEventRecord,
} from '../src/server/runtime-event-queue.js'

afterEach(() => {
  vi.useRealTimers()
})

describe('WorkflowAgentSessionReporter - queue-driven failure semantics', () => {
  function buildReporter(
    failEnqueueBeforeExecution = false,
    failEnqueueProducedFact = false,
    receiptError: Error | null = null,
    cleanupAttempt?: number,
    inputReceiptBudgetMs?: number,
    signal?: AbortSignal,
  ) {
    const records: RuntimeEventRecord[] = []
    const receiptWaits: Array<{ recordId: string; options?: RuntimeEventInputReceiptWaitOptions }> = []
    const beforeExecutionCalls: RuntimeEventRecord[] = []
    const producedFactCalls: RuntimeEventRecord[] = []
    const producedFactSingleCalls: RuntimeEventRecord[] = []
    const producedFactBatchCalls: RuntimeEventRecord[][] = []
    const outbox: AgentSessionRuntimeEventQueue = {
      ready: () => true,
      load: async () => {},
      recover: async () => {},
      async enqueueBeforeExecution(record) {
        beforeExecutionCalls.push(record as RuntimeEventRecord)
        if (failEnqueueBeforeExecution) throw new Error('input snapshot failed')
        records.push(record as RuntimeEventRecord)
      },
      async awaitInputReceipt(recordId, options) {
        receiptWaits.push({ recordId, options })
        if (receiptError) throw receiptError
        const cleanupStart = recordId.startsWith('workflow-cleanup:') && !recordId.endsWith(':runtime-input')
        return {
          type: cleanupStart ? 'session.cleanup' : 'session.input',
          inputDeliveryId: cleanupStart ? `workflow-cleanup-input:${recordId}` : recordId,
          agentTurnId: cleanupStart ? `workflow-cleanup-turn:${recordId}` : `turn-${recordId}`,
          agentSessionId: 'agent-session-1',
        }
      },
      async enqueueProducedFact(record) {
        producedFactSingleCalls.push(record as RuntimeEventRecord)
        producedFactCalls.push(record as RuntimeEventRecord)
        if (failEnqueueProducedFact) throw new Error('produced-fact snapshot failed')
        records.push(record as RuntimeEventRecord)
      },
      async enqueueProducedFactBatch(batch) {
        producedFactBatchCalls.push([...batch] as RuntimeEventRecord[])
        for (const record of batch) {
          producedFactCalls.push(record as RuntimeEventRecord)
        }
        if (failEnqueueProducedFact) throw new Error('produced-fact snapshot failed')
        for (const record of batch) {
          records.push(record as RuntimeEventRecord)
        }
      },
      kick: async () => {},
      stop: async () => {},
      snapshot() {
        return records
      },
    }
    const reporter = new WorkflowAgentSessionReporter({
      outbox,
      projectId: 'proj-1',
      workflowRunId: 'wf-1',
      sessionName: 'plan',
      workMetadata: {
        workId: 'work-1',
        taskRunId: 'task-1.1',
        runnerId: 'runner-1',
        agentSessionId: 'agent-session-1',
        workType: 'task',
        stage: 'plan',
      },
      runtime: 'opencode',
      randomId: (() => {
        let counter = 0
        return () => `id_${++counter}`
      })(),
      inputReceiptBudgetMs,
      signal,
      cleanupAttempt,
    })
    return {
      reporter,
      outbox,
      records,
      beforeExecutionCalls,
      producedFactCalls,
      producedFactSingleCalls,
      producedFactBatchCalls,
      receiptWaits,
    }
  }

  it('settles after all queued produced-fact enqueues, including a rejected input', async () => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    try {
      const { reporter } = buildReporter(true)
      await expect(reporter.awaitInput('p', 'ses_1')).rejects.toThrow(/input snapshot failed/)
      expect(reporter.inputWasAccepted()).toBe(false)
      expect(reporter.inputWasRejected()).toBe(true)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it('rejected input suppresses later close reports', async () => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    try {
      const { reporter, producedFactCalls } = buildReporter(true)
      await expect(reporter.awaitInput('p', 'ses_1')).rejects.toThrow()
      reporter.registerEvent({
        type: 'message.delta',
        runtimeSessionId: 'ses_1',
        workDir: '/w',
        payload: { text: 'x' },
      })
      reporter.registerClose({ status: 'completed', exitCode: 0, runtimeSessionId: 'ses_1' })
      await reporter.settle()
      expect(producedFactCalls).toHaveLength(0)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it('fails closed on an already-consumed input without inventing an Agent turn', async () => {
    const { reporter } = buildReporter(false, false, new AlreadyConsumedRuntimeEventError('input-1'))
    await expect(reporter.awaitInput('p', 'ses_1')).rejects.toMatchObject({
      classification: 'already-consumed',
      recordId: 'input-1',
    })
    expect(reporter.getAgentTurnId()).toBeNull()
    expect(reporter.inputWasRejected()).toBe(true)
    expect(reporter.inputWasAccepted()).toBe(false)
  })

  it('fails closed on an already-consumed cleanup without enqueuing follow-up input', async () => {
    const { reporter, records } = buildReporter(
      false,
      false,
      new AlreadyConsumedRuntimeEventError('workflow-cleanup:wf-1:task-1.1:work-1:1'),
      1,
    )
    await expect(reporter.awaitInput('clean', 'ses_1')).rejects.toMatchObject({ classification: 'already-consumed' })
    expect(reporter.getAgentTurnId()).toBeNull()
    expect(reporter.inputWasRejected()).toBe(true)
    expect(records).toHaveLength(1)
    reporter.registerEvent({
      type: 'message.delta',
      runtimeSessionId: 'ses_1',
      workDir: '/w',
      payload: { text: 'ignored' },
    })
    expect(records).toHaveLength(1)
  })

  it('continues after a later produced-fact rejection when input was accepted', async () => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    try {
      const { reporter, producedFactCalls } = buildReporter(false, true)
      await reporter.awaitInput('p', 'ses_1')
      reporter.registerEvent({
        type: 'message.delta',
        runtimeSessionId: 'ses_1',
        workDir: '/w',
        payload: { text: 'x' },
      })
      reporter.registerEvent({
        type: 'message.delta',
        runtimeSessionId: 'ses_1',
        workDir: '/w',
        payload: { text: 'y' },
      })
      await expect(reporter.settle()).resolves.toBeUndefined()
      expect(producedFactCalls.map((r) => r.event.type)).toEqual(['message.delta', 'message.delta'])
      expect(reporter.inputWasAccepted()).toBe(true)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it('does not pass the ordinary receipt budget or signal to cleanup waits', async () => {
    const signal = new AbortController().signal
    const { reporter, receiptWaits, records } = buildReporter(false, false, null, 1, 1234, signal)

    await reporter.awaitInput('clean', 'ses_1')

    expect(receiptWaits).toHaveLength(2)
    expect(receiptWaits.map((wait) => wait.options)).toEqual([undefined, undefined])
    expect(records.map((record) => record.event.type)).toEqual(['session.cleanup', 'session.input'])
  })

  it('stamps every later runtime event with the acknowledged Workflow turn', async () => {
    const { reporter, beforeExecutionCalls, producedFactCalls } = buildReporter()

    await reporter.awaitInput('p', 'ses_1')
    reporter.registerEvent({
      type: 'tool_call.started',
      runtimeSessionId: 'ses_1',
      workDir: '/w',
      payload: { tool: 'test' },
    })
    await reporter.settle()

    const input = beforeExecutionCalls[0]
    const fact = producedFactCalls[0]
    if (!input || !fact) throw new Error('expected input and produced runtime event')
    expect(input.event.type).toBe('session.input')
    expect(fact.event.type).toBe('tool_call.started')
    expect(input.work).toMatchObject({
      taskRunId: 'task-1.1',
      inputDeliveryId: input.id,
      agentTurnId: null,
    })
    expect(fact.work).toMatchObject({
      taskRunId: 'task-1.1',
      inputDeliveryId: input.id,
      agentTurnId: `turn-${input.id}`,
    })
    expect(fact.event.payload).toMatchObject({ turnId: `turn-${input.id}` })
  })

  it('buffers streaming deltas and flushes them as one batch before the close fact', async () => {
    const { reporter, producedFactCalls, producedFactSingleCalls, producedFactBatchCalls } = buildReporter()
    await reporter.awaitInput('p', 'ses_1')
    reporter.registerEvent({
      type: 'reasoning.delta',
      runtimeSessionId: 'ses_1',
      workDir: '/w',
      payload: { text: 'a' },
    })
    reporter.registerEvent({
      type: 'reasoning.delta',
      runtimeSessionId: 'ses_1',
      workDir: '/w',
      payload: { text: 'b' },
    })
    reporter.registerEvent({
      type: 'reasoning.delta',
      runtimeSessionId: 'ses_1',
      workDir: '/w',
      payload: { text: 'c' },
    })
    reporter.registerClose({ status: 'completed', exitCode: 0, runtimeSessionId: 'ses_1' })
    await reporter.settle()

    expect(producedFactSingleCalls).toHaveLength(0)
    expect(producedFactBatchCalls.map((batch) => batch.map((record) => record.event.type))).toEqual([
      ['reasoning.delta', 'reasoning.delta', 'reasoning.delta'],
      ['session.activity'],
    ])
    expect(producedFactCalls.map((r) => r.event.type)).toEqual([
      'reasoning.delta',
      'reasoning.delta',
      'reasoning.delta',
      'session.activity',
    ])
  })

  it('flushes buffered deltas when a non-delta event arrives mid-turn', async () => {
    const { reporter, producedFactCalls, producedFactSingleCalls, producedFactBatchCalls } = buildReporter()
    await reporter.awaitInput('p', 'ses_1')
    reporter.registerEvent({
      type: 'reasoning.delta',
      runtimeSessionId: 'ses_1',
      workDir: '/w',
      payload: { text: 'a' },
    })
    reporter.registerEvent({ type: 'tool_call.started', runtimeSessionId: 'ses_1', workDir: '/w', payload: {} })
    reporter.registerEvent({
      type: 'reasoning.delta',
      runtimeSessionId: 'ses_1',
      workDir: '/w',
      payload: { text: 'b' },
    })
    reporter.registerClose({ status: 'completed', exitCode: 0, runtimeSessionId: 'ses_1' })
    await reporter.settle()

    expect(producedFactSingleCalls.map((record) => record.event.type)).toEqual(['tool_call.started'])
    expect(producedFactBatchCalls.map((batch) => batch.map((record) => record.event.type))).toEqual([
      ['reasoning.delta'],
      ['reasoning.delta'],
      ['session.activity'],
    ])
    expect(producedFactCalls.map((r) => r.event.type)).toEqual([
      'reasoning.delta',
      'tool_call.started',
      'reasoning.delta',
      'session.activity',
    ])
  })

  it('bounds streaming delta batches before a turn ends', async () => {
    const { reporter, producedFactSingleCalls, producedFactBatchCalls } = buildReporter()
    await reporter.awaitInput('p', 'ses_1')
    for (let index = 0; index < 300; index += 1) {
      reporter.registerEvent({
        type: 'message.delta',
        runtimeSessionId: 'ses_1',
        workDir: '/w',
        payload: { text: String(index) },
      })
    }
    reporter.registerClose({ status: 'completed', exitCode: 0, runtimeSessionId: 'ses_1' })
    await reporter.settle()

    expect(producedFactSingleCalls).toHaveLength(0)
    expect(producedFactBatchCalls.map((batch) => batch.length)).toEqual([256, 44, 1])
    expect(producedFactBatchCalls.at(-1)?.[0].event.type).toBe('session.activity')
  })
})
