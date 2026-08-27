import { afterEach, describe, expect, it, vi } from 'vitest'
import { piAction, PI_TURN_DURATION_MS } from './pi.js'
import type { AgentExecutionDefinition, JsonObject, ParentIssueContext } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { PiResult, PiRuntime, PiTurnResult } from '../runtime/pi/index.js'
import {
  InputReceiptWaitCancelledError,
  InputReceiptWaitTimeoutError,
  createAgentSessionRuntimeEventQueue,
  type AgentSessionRuntimeEventQueue,
  type RuntimeEventRecord,
} from '../server/runtime-event-queue.js'

type ActionContext = {
  workflowRunId: string
  workId: string
  workType: string
  variables: JsonObject
  workDir: string
  signal: AbortSignal
  with?: JsonObject | null
  writeVars: (vars: JsonObject) => Promise<void>
  projectId?: string | null
  parentIssueContext?: ParentIssueContext | null
  actionAttemptId?: string | null
  runnerId?: string | null
  runtimeEventQueue?: AgentSessionRuntimeEventQueue | null
  runtimeEventRecordId?: () => string
  piRuntime?: PiRuntime | null
  serverConnection?: ServerConnection | null
  agentDefinition?: AgentExecutionDefinition | null
}

function context(overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    workflowRunId: 'run-1',
    workId: 'work-1',
    workType: 'task',
    variables: {},
    workDir: '/workspace',
    signal: new AbortController().signal,
    with: { prompt: 'return <promise>PASS</promise>', session: ' shared ' },
    writeVars: async () => {},
    ...overrides,
  }
}

function runtime() {
  const turns: unknown[] = []
  return {
    turns,
    ready: () => true,
    diagnostic: () => null,
    createSession: vi.fn(async () => ({
      ok: true as const,
      value: { runtimeSessionId: '/workspace/.pi/session.json', workDir: '/workspace' },
      diagnostics: [],
    })),
    runTurn: vi.fn(
      async (
        request: { durationMs?: number },
        _signal: AbortSignal,
        observer?: { onEvent?: (event: unknown) => void },
      ): Promise<PiResult<PiTurnResult>> => {
        turns.push(request)
        observer?.onEvent?.({
          id: 'assistant-1',
          type: 'assistant.text',
          runtimeSessionId: '/workspace/.pi/session.json',
          workDir: '/workspace',
          payload: { content: 'done' },
        })
        return {
          ok: true as const,
          value: {
            facts: {
              finalAssistantText: 'return <promise>PASS</promise>',
              runtimeSessionId: '/workspace/.pi/session.json',
              workDir: '/workspace',
            },
            diagnostics: [],
          },
          diagnostics: [],
        }
      },
    ),
  }
}

afterEach(() => {
  vi.useRealTimers()
})

async function flushMicrotasks(count = 8): Promise<void> {
  for (let index = 0; index < count; index += 1) await Promise.resolve()
}

function server() {
  const calls: unknown[] = []
  return {
    calls,
    openWorkflowAgentSession: vi.fn(async () => ({ runtime: 'pi', runtimeSessionId: null, workDir: '/workspace' })),
    attachWorkflowAgentSession: vi.fn(async () => ({
      runtime: 'pi',
      runtimeSessionId: '/workspace/.pi/session.json',
      workDir: '/workspace',
    })),
    workflowAgentSessionRuntimeEvents: vi.fn(async (_project: string, _run: string, _name: string, body: unknown) => {
      calls.push(body)
      return [{ id: 'accepted' }]
    }),
  }
}

describe('mohist/pi Action', () => {
  it('rejects undeclared top-level input before Session or runtime side effects', async () => {
    const pi = runtime()
    const connection = server()
    const result = await piAction(
      context({
        with: { prompt: 'hello', unexpected: 10 },
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )
    expect(result).toMatchObject({ error: { code: 'invalid-input' } })
    expect(connection.openWorkflowAgentSession).not.toHaveBeenCalled()
    expect(pi.createSession).not.toHaveBeenCalled()
  })

  it('binds, accepts input, submits a fixed-duration turn, and returns the Session to idle', async () => {
    const pi = runtime()
    const connection = server()
    const result = await piAction(
      context({ piRuntime: pi as never, serverConnection: connection as never, projectId: 'project' }),
    )
    expect(result).toMatchObject({ output: null, turnFact: { finalAssistantText: 'return <promise>PASS</promise>' } })
    expect(connection.attachWorkflowAgentSession).toHaveBeenCalledBefore(connection.workflowAgentSessionRuntimeEvents)
    expect(pi.turns[0]).toMatchObject({ durationMs: PI_TURN_DURATION_MS })
    expect(connection.workflowAgentSessionRuntimeEvents).toHaveBeenCalledTimes(2)
    expect((connection.calls[0] as { runtimeEvents: Array<{ type: string }> }).runtimeEvents[0].type).toBe(
      'session.input',
    )
    expect((connection.calls[1] as { runtimeEvents: Array<{ type: string }> }).runtimeEvents.at(-1)?.type).toBe(
      'session.activity',
    )
  })

  it('uses the declared Pi timeout for the turn duration', async () => {
    const pi = runtime()
    const connection = server()

    const result = await piAction(
      context({
        with: { prompt: 'return <promise>PASS</promise>', timeout: 12_345 },
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )

    expect(result).not.toHaveProperty('error')
    expect(pi.turns[0]).toMatchObject({ durationMs: 12_345 })
  })

  it('preserves an unexpected turn failure when terminal reporting also fails', async () => {
    const pi = runtime()
    const connection = server()
    pi.runTurn.mockRejectedValueOnce(new Error('SDK turn failed'))
    connection.workflowAgentSessionRuntimeEvents
      .mockImplementationOnce(async (_project: string, _run: string, _name: string, body: unknown) => {
        connection.calls.push(body)
        return [{ id: 'accepted' }]
      })
      .mockImplementationOnce(async (_project: string, _run: string, _name: string, body: unknown) => {
        connection.calls.push(body)
        throw new Error('terminal report rejected')
      })

    const result = await piAction(
      context({ piRuntime: pi as never, serverConnection: connection as never, projectId: 'project' }),
    )

    expect(result).toMatchObject({
      error: {
        code: 'turn-failed',
        message: 'SDK turn failed; Session terminal reporting failed and terminal state was not accepted',
      },
    })
    expect(connection.workflowAgentSessionRuntimeEvents).toHaveBeenCalledTimes(2)
    expect((connection.calls[1] as { runtimeEvents: Array<{ type: string }> }).runtimeEvents.at(-1)?.type).toBe(
      'session.activity',
    )
  })

  it('marks an unconfirmed Pi cleanup as unknown instead of an authoritative failure', async () => {
    const pi = runtime()
    const connection = server()
    pi.runTurn.mockResolvedValueOnce({
      ok: false,
      error: {
        kind: 'deadline-exceeded',
        message: 'Pi turn deadline exceeded',
        diagnostics: [{ severity: 'error', code: 'abort-unconfirmed', message: 'Pi did not confirm stop' }],
      },
      diagnostics: [{ severity: 'error', code: 'abort-unconfirmed', message: 'Pi did not confirm stop' }],
    })

    const result = await piAction(
      context({ piRuntime: pi as never, serverConnection: connection as never, projectId: 'project' }),
    )

    expect(result).toMatchObject({ error: { code: 'timeout' }, outcome: 'unknown' })
    const terminal = (
      connection.calls[1] as { runtimeEvents: Array<{ type: string; payload: { status?: string } }> }
    ).runtimeEvents.find((event) => event.type === 'turn.failed')
    expect(terminal?.payload.status).toBe('unknown')
  })

  it('keeps unknown options diagnostic-only', async () => {
    const pi = runtime()
    const connection = server()
    const result = await piAction(
      context({
        with: { prompt: 'hello', options: { model: 'provider/model', variant: 'high', legacy: true } },
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )
    expect(result).not.toHaveProperty('error')
    expect(pi.turns[0]).toMatchObject({
      options: { model: 'provider/model', variant: 'high', unknownKeys: ['legacy'] },
    })
  })

  it('accepts the frozen reasoning effort in options and forwards it to the turn beside model and variant', async () => {
    // Issue-557 T-002: `vars.agent.reasoningEffort` reaches the mohist/pi
    // dispatch `options`; the effort is a known tuple member (never an
    // unknown-key diagnostic) and is forwarded into the Pi turn options.
    const pi = runtime()
    const connection = server()
    const result = await piAction(
      context({
        with: { prompt: 'hello', options: { model: 'provider/model', variant: 'balanced', reasoningEffort: 'high' } },
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )
    expect(result).not.toHaveProperty('error')
    expect(pi.turns[0]).toMatchObject({
      options: { model: 'provider/model', variant: 'balanced', reasoningEffort: 'high', unknownKeys: undefined },
    })
  })

  it('rejects a non-string reasoning effort option like a non-string variant', async () => {
    const pi = runtime()
    const connection = server()
    const result = await piAction(
      context({
        with: { prompt: 'hello', options: { reasoningEffort: 42 as never } },
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )
    expect(result).toMatchObject({ error: { code: 'invalid-input' } })
    expect(result.error?.message).toMatch(/options\.reasoningEffort.*must be a string/)
    expect(pi.createSession).not.toHaveBeenCalled()
  })

  it('uses the dispatch-only Agent definition without expanding Action options', async () => {
    const pi = runtime()
    const connection = server()
    const definition: AgentExecutionDefinition = {
      instructions: 'Review with the configured policy.',
      runtime: 'pi',
      model: 'provider/configured-model',
      variant: 'high',
      skills: [],
    }

    const result = await piAction(
      context({
        with: { prompt: 'review this', options: { model: 'provider/caller-model', variant: 'low' } },
        agentDefinition: definition,
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )

    expect(result).not.toHaveProperty('error')
    expect(pi.turns[0]).toMatchObject({
      prompt: 'Review with the configured policy.\n\nreview this',
      options: { model: 'provider/configured-model', variant: 'high' },
    })
  })

  it("freezes the Agent definition's reasoning effort over the caller option, with the option used when unset", async () => {
    // Issue-557 T-002: the frozen Agent definition (resolved at dispatch
    // translation, AgentExecutionDefinition.ReasoningEffort) wins over the
    // workflow options; without a definition the caller option is used;
    // absent everywhere means null (unset), never synthesized.
    const pi = runtime()
    const connection = server()
    const definition: AgentExecutionDefinition = {
      instructions: 'Review with the configured policy.',
      runtime: 'pi',
      model: 'provider/configured-model',
      variant: 'balanced',
      reasoningEffort: 'high',
      skills: [],
    }

    await piAction(
      context({
        with: { prompt: 'review this', options: { reasoningEffort: 'low' } },
        agentDefinition: definition,
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )
    expect(pi.turns[0]).toMatchObject({ options: { reasoningEffort: 'high' } })

    const callerOnly = runtime()
    const callerConnection = server()
    await piAction(
      context({
        with: { prompt: 'review this', options: { reasoningEffort: 'low' } },
        piRuntime: callerOnly as never,
        serverConnection: callerConnection as never,
        projectId: 'project',
      }),
    )
    expect(callerOnly.turns[0]).toMatchObject({ options: { reasoningEffort: 'low' } })

    const unset = runtime()
    const unsetConnection = server()
    await piAction(
      context({
        with: { prompt: 'review this' },
        piRuntime: unset as never,
        serverConnection: unsetConnection as never,
        projectId: 'project',
      }),
    )
    expect(unset.turns[0]).toMatchObject({ options: { reasoningEffort: null } })
  })

  it('recovers a retryable Pi input before the budget and invokes the runtime exactly once', async () => {
    vi.useFakeTimers()
    const pi = runtime()
    const connection = server()
    connection.openWorkflowAgentSession.mockResolvedValueOnce({
      sessionId: 'agent-session-pi',
      runtime: 'pi',
      runtimeSessionId: null,
      workDir: '/workspace',
    } as never)
    let inputAttempts = 0
    const queue = createAgentSessionRuntimeEventQueue({
      retryDelayMs: 100,
      deliver: {
        async send(record) {
          if (record.event.type === 'session.input') {
            inputAttempts += 1
            if (inputAttempts === 1) return [{ type: 'message.delta' }]
            return [
              {
                type: 'session.input',
                inputDeliveryId: record.id,
                agentTurnId: 'turn-pi-recovered',
                agentSessionId: record.work?.agentSessionId ?? undefined,
              },
            ]
          }
          return [{ type: record.event.type }]
        },
      },
    })
    await queue.load()
    const execution = piAction(
      context({
        with: { prompt: 'recover Pi input', timeout: 1_000 },
        actionAttemptId: 'task-pi',
        runnerId: 'runner-pi',
        runtimeEventQueue: queue,
        runtimeEventRecordId: () => 'pi-recovery-input',
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )

    await flushMicrotasks()
    expect(inputAttempts).toBe(1)
    expect(pi.runTurn).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(100)
    const result = await execution

    expect(result).not.toHaveProperty('error')
    expect(inputAttempts).toBe(2)
    expect(pi.runTurn).toHaveBeenCalledTimes(1)
    await queue.stop()
  })

  it('projects bounded input receipt expiry as session-reporting-failed without invoking Pi', async () => {
    const pi = runtime()
    const connection = server()
    connection.openWorkflowAgentSession.mockResolvedValueOnce({
      sessionId: 'agent-session-1',
      runtime: 'pi',
      runtimeSessionId: null,
      workDir: '/workspace',
    } as never)
    const records: RuntimeEventRecord[] = []
    const receiptError = new InputReceiptWaitTimeoutError(
      'input-1',
      { attempts: 3, retries: 2, lastReason: 'retryable: temporary server refusal' },
      250,
      250,
    )
    const outbox: AgentSessionRuntimeEventQueue = {
      ready: () => true,
      load: async () => {},
      recover: async () => {},
      async enqueueBeforeExecution(record) {
        records.push(record)
      },
      async awaitInputReceipt() {
        throw receiptError
      },
      async enqueueProducedFact() {},
      async enqueueProducedFactBatch() {},
      kick: async () => {},
      stop: async () => {},
      snapshot: () => records,
    }

    const result = await piAction(
      context({
        with: { prompt: 'wait for Pi input', timeout: 250 },
        actionAttemptId: 'task-1',
        runnerId: 'runner-1',
        runtimeEventQueue: outbox,
        runtimeEventRecordId: () => 'input-1',
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )

    expect(result).toMatchObject({ error: { code: 'session-reporting-failed' } })
    expect(result.error?.message).toContain('last reason: retryable: temporary server refusal')
    expect(result.error?.message).toContain('elapsed 250ms of 250ms')
    expect(result.error?.message).toContain('delivery attempts: 3; retries: 2')
    expect(pi.runTurn).not.toHaveBeenCalled()
    expect(records).toHaveLength(1)
  })

  it('projects receipt-wait cancellation as session-reporting-failed without invoking Pi', async () => {
    const pi = runtime()
    const connection = server()
    connection.openWorkflowAgentSession.mockResolvedValueOnce({
      sessionId: 'agent-session-1',
      runtime: 'pi',
      runtimeSessionId: null,
      workDir: '/workspace',
    } as never)
    const records: RuntimeEventRecord[] = []
    const receiptError = new InputReceiptWaitCancelledError('input-1', new Error('task cancellation'))
    const outbox: AgentSessionRuntimeEventQueue = {
      ready: () => true,
      load: async () => {},
      recover: async () => {},
      async enqueueBeforeExecution(record) {
        records.push(record)
      },
      async awaitInputReceipt() {
        throw receiptError
      },
      async enqueueProducedFact() {},
      async enqueueProducedFactBatch() {},
      kick: async () => {},
      stop: async () => {},
      snapshot: () => records,
    }

    const result = await piAction(
      context({
        with: { prompt: 'cancel Pi input', timeout: 1_000 },
        actionAttemptId: 'task-1',
        runnerId: 'runner-1',
        runtimeEventQueue: outbox,
        runtimeEventRecordId: () => 'input-1',
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )

    expect(result).toMatchObject({ error: { code: 'session-reporting-failed' } })
    expect(result.error?.message).toContain('task cancellation')
    expect(pi.runTurn).not.toHaveBeenCalled()
  })

  it('passes the effective Pi receipt budget and task signal to the reporter', async () => {
    const pi = runtime()
    const connection = server()
    connection.openWorkflowAgentSession.mockResolvedValueOnce({
      sessionId: 'agent-session-1',
      runtime: 'pi',
      runtimeSessionId: null,
      workDir: '/workspace',
    } as never)
    const records: RuntimeEventRecord[] = []
    const waits: Array<{
      recordId: string
      options?: { readonly budgetMs: number; readonly signal: AbortSignal }
    }> = []
    const outbox: AgentSessionRuntimeEventQueue = {
      ready: () => true,
      load: async () => {},
      recover: async () => {},
      async enqueueBeforeExecution(record) {
        records.push(record)
      },
      async awaitInputReceipt(recordId, options) {
        waits.push({ recordId, options })
        return {
          type: 'session.input',
          inputDeliveryId: recordId,
          agentTurnId: 'turn-1',
          agentSessionId: 'agent-session-1',
        }
      },
      async enqueueProducedFact() {},
      async enqueueProducedFactBatch() {},
      kick: async () => {},
      stop: async () => {},
      snapshot: () => records,
    }
    const controller = new AbortController()

    const result = await piAction(
      context({
        signal: controller.signal,
        with: { prompt: 'run Pi', timeout: 12_345 },
        actionAttemptId: 'task-1',
        runnerId: 'runner-1',
        runtimeEventQueue: outbox,
        runtimeEventRecordId: () => 'input-1',
        piRuntime: pi as never,
        serverConnection: connection as never,
        projectId: 'project',
      }),
    )

    expect(result).not.toHaveProperty('error')
    expect(waits).toHaveLength(1)
    expect(waits[0]).toMatchObject({ recordId: 'input-1', options: { budgetMs: 12_345 } })
    expect(waits[0]?.options?.signal).toBe(controller.signal)
    expect(pi.runTurn).toHaveBeenCalledTimes(1)
  })
})
