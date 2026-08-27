import { afterEach, describe, expect, it, vi } from 'vitest'
import { opencodeAction } from '../src/actions/opencode.js'
import { OpenCodeRuntime } from '../src/runtime/opencode/index.js'
import type { OpenCodeRuntimeDeps } from '../src/runtime/opencode/runtime.js'
import type { OpencodeServerHandle } from '../src/runtime/opencode/server-process.js'
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from '../src/runtime/opencode/event-subscription.js'
import type { OpencodeClient } from '@opencode-ai/sdk/v2'
import type { AgentSessionRuntimeEventReceipt } from '../src/server/connection.js'
import type { ActionTestContext as ActionContext } from './support/action-test-context.js'
import {
  InputReceiptWaitCancelledError,
  InputReceiptWaitTimeoutError,
  createAgentSessionRuntimeEventQueue,
  type AgentSessionRuntimeEventQueue,
  type RuntimeEventRecord,
} from '../src/server/runtime-event-queue.js'
import { makeRecordingOutbox, type OutboxHandles } from './support/outbox-test-helpers.js'
import { callAction } from './support/call-action.js'

class FakeSubscription implements RuntimeEventSubscription {
  private listeners = new Set<(event: RuntimeGlobalEvent) => void>()
  closed = false
  subscribe(listener: (event: RuntimeGlobalEvent) => void): () => void {
    if (this.closed) return () => {}
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }
  emit(event: RuntimeGlobalEvent): void {
    for (const listener of [...this.listeners]) listener(event)
  }
  async close(): Promise<void> {
    this.closed = true
    this.listeners.clear()
  }
}

interface BuildArgs {
  promptResult?: unknown
  failCreate?: boolean
  failPrompt?: boolean
  failPromptMessage?: string
  emitDuringPrompt?: (subscription: FakeSubscription, sessionId: string) => Promise<void> | void
}

interface BuildResult {
  deps: OpenCodeRuntimeDeps
  runtime: OpenCodeRuntime
  client: {
    sessionCreate: ReturnType<typeof vi.fn>
    sessionPrompt: ReturnType<typeof vi.fn>
    sessionPromptAsync: ReturnType<typeof vi.fn>
    sessionAbort: ReturnType<typeof vi.fn>
    sessionGet: ReturnType<typeof vi.fn>
    sessionStatus: ReturnType<typeof vi.fn>
  }
  subscription: FakeSubscription
}

function buildRuntime(args: BuildArgs = {}): BuildResult {
  const subscription = new FakeSubscription()
  const sessionCreate = vi.fn(async () => ({ data: { id: 'ses_bound' } }))
  const sessionPrompt = vi.fn(async (params: { sessionID: string; directory?: string; parts?: unknown }) => {
    if (args.emitDuringPrompt) {
      await args.emitDuringPrompt(subscription, params.sessionID)
    }
    if (args.failPrompt) throw new Error(args.failPromptMessage ?? 'opencode prompt failed')
    if (args.promptResult !== undefined) return args.promptResult
    return {
      data: {
        info: {
          id: 'msg_1',
          sessionID: 'ses_bound',
          role: 'assistant',
          providerID: 'openai',
          modelID: 'gpt-5.6',
          tokens: { input: 0, output: 0, total: 0, reasoning: 0, cache: { read: 0, write: 0 } },
          cost: 0,
        },
        parts: [],
      },
    }
  })
  const sessionPromptAsync = vi.fn(async () => undefined)
  const sessionAbort = vi.fn(async () => undefined)
  const sessionGet = vi.fn(async () => ({ data: { id: 'ses_bound' } }))
  const sessionStatus = vi.fn(async () => ({ data: {} }))
  const client: OpencodeClient = {
    global: { health: vi.fn(async () => ({ data: { ok: true } })), event: vi.fn() },
    session: {
      create: sessionCreate,
      prompt: sessionPrompt,
      promptAsync: sessionPromptAsync,
      abort: sessionAbort,
      get: sessionGet,
      status: sessionStatus,
    },
  } as unknown as OpencodeClient
  const serverHandle: OpencodeServerHandle = {
    url: 'http://fake',
    directory: '/tmp/work',
    client,
    async close() {
      subscription.closed = true
    },
  }
  const deps: OpenCodeRuntimeDeps = {
    directory: '/tmp/work',
    serverFactory: async () => serverHandle,
    eventSubscriptionFactory: () => subscription,
  }
  const runtime = new OpenCodeRuntime(deps)
  void runtime.start()
  return {
    deps,
    runtime,
    client: { sessionCreate, sessionPrompt, sessionPromptAsync, sessionAbort, sessionGet, sessionStatus },
    subscription,
  }
}

async function ensureReady(runtime: OpenCodeRuntime): Promise<void> {
  await runtime.start()
}

function baseContext(overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    workflowRunId: 'wf-1',
    workId: 'work-1',
    workType: 'task',
    stage: 'plan',
    title: 'Workflow turn',
    uses: 'mohist/opencode',
    with: { prompt: 'do the work', session: 'plan' } as never,
    variables: {},
    workDir: '/tmp/work',
    signal: new AbortController().signal,
    projectId: 'proj-1',
    writeVars: async () => {},
    ...overrides,
  }
}

afterEach(() => {
  vi.useRealTimers()
})

async function flushMicrotasks(count = 8): Promise<void> {
  for (let index = 0; index < count; index += 1) await Promise.resolve()
}

async function emitStandardSequence(subscription: FakeSubscription, sessionId: string): Promise<void> {
  subscription.emit({
    type: 'message.updated',
    sessionID: sessionId,
    payload: {
      info: {
        id: 'msg_1',
        role: 'assistant',
        providerID: 'openai',
        modelID: 'gpt-5',
        tokens: { input: 5, output: 7, total: 12, reasoning: 0, cache: { read: 0 } },
        cost: 0.0001,
      },
    },
  })
  subscription.emit({
    type: 'session.next.reasoning.delta',
    sessionID: sessionId,
    payload: { reasoningID: 'r_1', assistantMessageID: 'msg_1', delta: 'thinking... ' },
  })
  subscription.emit({
    type: 'session.next.tool.called',
    sessionID: sessionId,
    payload: { callID: 'tool_1', tool: 'read', input: { path: '/etc' } },
  })
  subscription.emit({
    type: 'session.next.tool.progress',
    sessionID: sessionId,
    payload: { callID: 'tool_1', tool: 'read', input: { path: '/etc' } },
  })
  subscription.emit({
    type: 'session.next.tool.success',
    sessionID: sessionId,
    payload: { callID: 'tool_1', tool: 'read', result: 'ok' },
  })
  subscription.emit({
    type: 'message.part.updated',
    sessionID: sessionId,
    payload: { part: { id: 'txt_1', messageID: 'msg_1', type: 'text', text: 'hi' } },
  })
  await new Promise((resolve) => setImmediate(resolve))
}

function makeFailureOutbox(): AgentSessionRuntimeEventQueue {
  return {
    ready: () => true,
    load: async () => {},
    recover: async () => {},
    async enqueueBeforeExecution() {
      throw new Error('disk full (input)')
    },
    async enqueueProducedFact() {
      throw new Error('disk full (produced)')
    },
    async enqueueProducedFactBatch() {
      throw new Error('disk full (produced)')
    },
    kick: async () => {},
    stop: async () => {},
    snapshot() {
      return []
    },
  }
}

function makeProducedFactFailureOutbox(): AgentSessionRuntimeEventQueue {
  return {
    ready: () => true,
    load: async () => {},
    recover: async () => {},
    async enqueueBeforeExecution() {},
    async awaitInputReceipt(recordId) {
      return {
        type: 'session.input',
        inputDeliveryId: recordId,
        agentTurnId: `turn-${recordId}`,
        agentSessionId: 'agent-session-test',
      }
    },
    async enqueueProducedFact() {
      throw new Error('disk full (produced)')
    },
    async enqueueProducedFactBatch() {
      throw new Error('disk full (produced)')
    },
    kick: async () => {},
    stop: async () => {},
    snapshot() {
      return []
    },
  }
}

describe('opencodeAction — Workflow AgentSession transcript reporting', () => {
  it('enqueues the composed prompt as session.input and forwards projected events in production order', async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: emitStandardSequence,
    })
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventQueue: handles.outbox,
    })

    const result = await callAction(opencodeAction, context)

    expect(result.error).toBeUndefined()
    const types = handles.eventTypeList()
    expect(types[0]).toBe('session.input')
    expect(types).toEqual(
      expect.arrayContaining([
        'model.resolved',
        'usage.updated',
        'reasoning.delta',
        'tool_call.started',
        'tool_call.updated',
        'tool_call.completed',
        'message.delta',
      ]),
    )
    expect(handles.eventsByType('session.input')[0]).toMatchObject({
      event: {
        type: 'session.input',
        payload: expect.objectContaining({
          text: 'do the work',
          kind: 'task',
          source: 'workflow',
          role: 'user',
          runtimeSessionId: 'ses_bound',
        }),
      },
    })
    const closeEvent = handles.eventsByType('session.activity')[0]
    expect(closeEvent?.event.payload).toMatchObject({ status: 'completed', exitCode: 0 })
  })

  it('does not reproject SDK payloads; reporter receives runtime event payloads as-is', async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: 'session.next.tool.called',
          sessionID: sessionId,
          payload: { callID: 'tool_2', tool: 'bash', input: { cmd: 'ls' } },
        })
        subscription.emit({
          type: 'session.next.tool.failed',
          sessionID: sessionId,
          payload: { callID: 'tool_2', tool: 'bash', error: 'boom' },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventQueue: handles.outbox,
    })
    await callAction(opencodeAction, context)

    const started = handles.eventsByType('tool_call.started')[0]
    const failed = handles.eventsByType('tool_call.completed')[0]
    expect(started?.event.payload).toMatchObject({
      toolCallId: 'tool_2',
      toolName: 'bash',
      rawInput: { cmd: 'ls' },
    })
    expect(failed?.event.payload).toMatchObject({
      toolCallId: 'tool_2',
      toolName: 'bash',
      status: 'failed',
      state: 'failed',
    })
    expect(handles.eventsByType('turn.failed')).toHaveLength(0)
  })

  it('serializes input and projected event enqueues in observation order', async () => {
    const { runtime } = buildRuntime({
      promptResult: {
        data: {
          info: { id: 'msg_1', sessionID: 'ses_bound', role: 'assistant' },
          parts: [],
        },
      },
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: 'session.next.text.delta',
          sessionID: sessionId,
          payload: { textID: 'txt_a', assistantMessageID: 'msg_1', delta: 'alpha ' },
        })
        subscription.emit({
          type: 'session.next.text.delta',
          sessionID: sessionId,
          payload: { textID: 'txt_b', assistantMessageID: 'msg_1', delta: 'beta' },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventQueue: handles.outbox,
    })
    await callAction(opencodeAction, context)

    expect(handles.eventTypeList()).toEqual(['session.input', 'message.delta', 'message.delta', 'session.activity'])
  })

  it('rejected input persistence returns execution-unavailable and never invokes OpenCodeRuntime.runTurn', async () => {
    const { runtime, client } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: 'message.part.updated',
          sessionID: sessionId,
          payload: { part: { id: 'txt_a', messageID: 'msg_1', type: 'text', text: 'alpha' } },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const outbox = makeFailureOutbox()
    const handles = makeRecordingOutbox()
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventQueue: outbox,
    })

    try {
      const result = await callAction(opencodeAction, context)
      expect(result.error?.code).toBe('execution-unavailable')
      expect(client.sessionPrompt).not.toHaveBeenCalled()
    } finally {
      errorSpy.mockRestore()
    }
  })

  it('rejected Server input receipt returns execution-unavailable and never invokes OpenCodeRuntime.runTurn', async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    handles.setInputAccepted(false)
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventQueue: handles.outbox,
    })

    try {
      const result = await callAction(opencodeAction, context)
      expect(result.error?.code).toBe('execution-unavailable')
      expect(client.sessionPrompt).not.toHaveBeenCalled()
      expect(handles.eventsByType('session.input')).toHaveLength(1)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it('projects bounded input receipt expiry as session-reporting-failed without invoking OpenCode', async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const receiptError = new InputReceiptWaitTimeoutError(
      'input-1',
      { attempts: 2, retries: 1, lastReason: 'retryable: temporary server refusal' },
      250,
      250,
    )
    const outbox: AgentSessionRuntimeEventQueue = {
      ...handles.outbox,
      async awaitInputReceipt() {
        throw receiptError
      },
    }
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    try {
      const result = await callAction(
        opencodeAction,
        baseContext({
          with: { prompt: 'wait for input', session: 'plan', timeout: 250 } as never,
          openCodeRuntime: runtime,
          serverConnection: handles.connection,
          agentSessionRuntimeEventQueue: outbox,
        }),
      )
      expect(result.error?.code).toBe('session-reporting-failed')
      expect(result.error?.message).toContain('last reason: retryable: temporary server refusal')
      expect(result.error?.message).toContain('elapsed 250ms of 250ms')
      expect(client.sessionPrompt).not.toHaveBeenCalled()
      expect(handles.eventsByType('session.input')).toHaveLength(1)
    } finally {
      errorSpy.mockRestore()
    }
  })

  it('projects receipt-wait cancellation as session-reporting-failed without invoking OpenCode', async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const receiptError = new InputReceiptWaitCancelledError('input-1', new Error('task cancellation'))
    const outbox: AgentSessionRuntimeEventQueue = {
      ...handles.outbox,
      async awaitInputReceipt() {
        throw receiptError
      },
    }
    const result = await callAction(
      opencodeAction,
      baseContext({
        with: { prompt: 'cancel OpenCode input', session: 'plan', timeout: 1_000 } as never,
        openCodeRuntime: runtime,
        serverConnection: handles.connection,
        agentSessionRuntimeEventQueue: outbox,
      }),
    )

    expect(result.error?.code).toBe('session-reporting-failed')
    expect(result.error?.message).toContain('task cancellation')
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })

  it('recovers a retryable OpenCode input before the budget and invokes the runtime exactly once', async () => {
    vi.useFakeTimers()
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
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
                agentTurnId: 'turn-open-code-recovered',
                agentSessionId: record.work?.agentSessionId ?? undefined,
              },
            ]
          }
          return [{ type: record.event.type }]
        },
      },
    })
    const handles = makeRecordingOutbox()
    const execution = callAction(
      opencodeAction,
      baseContext({
        with: { prompt: 'recover OpenCode input', session: 'plan', timeout: 1_000 } as never,
        openCodeRuntime: runtime,
        serverConnection: handles.connection,
        agentSessionRuntimeEventQueue: queue,
      }),
    )

    await flushMicrotasks()
    expect(inputAttempts).toBe(1)
    expect(client.sessionPrompt).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(100)
    const result = await execution

    expect(result.error).toBeUndefined()
    expect(inputAttempts).toBe(2)
    expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
    await queue.stop()
  })

  it('does not invoke OpenCode when a late receipt arrives after the task wait fails', async () => {
    vi.useFakeTimers()
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    let resolveLate!: (receipts: AgentSessionRuntimeEventReceipt[]) => void
    const lateReceipt = new Promise<AgentSessionRuntimeEventReceipt[]>((resolve) => {
      resolveLate = resolve
    })
    const queue = createAgentSessionRuntimeEventQueue({
      deliveryTimeoutMs: 1_000,
      retryDelayMs: 100,
      deliver: {
        async send() {
          return await lateReceipt
        },
      },
    })
    const handles = makeRecordingOutbox()
    const execution = callAction(
      opencodeAction,
      baseContext({
        with: { prompt: 'wait for a late OpenCode receipt', session: 'plan', timeout: 50 } as never,
        openCodeRuntime: runtime,
        serverConnection: handles.connection,
        agentSessionRuntimeEventQueue: queue,
      }),
    )

    await flushMicrotasks()
    const pending = queue.snapshot()[0]
    if (!pending) throw new Error('expected Workflow input before timeout')
    await vi.advanceTimersByTimeAsync(50)
    const result = await execution

    expect(result.error?.code).toBe('session-reporting-failed')
    expect(client.sessionPrompt).not.toHaveBeenCalled()
    expect(queue.snapshot()).toEqual([pending])

    resolveLate([
      {
        type: 'session.input',
        inputDeliveryId: pending.id,
        agentTurnId: 'late-turn',
        agentSessionId: pending.work?.agentSessionId ?? undefined,
      },
    ])
    await flushMicrotasks(12)

    expect(queue.snapshot()).toEqual([])
    expect(client.sessionPrompt).not.toHaveBeenCalled()
    await queue.stop()
  })

  it('passes the effective OpenCode receipt budget and task signal to the reporter', async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const waits: Array<{
      recordId: string
      options?: { readonly budgetMs: number; readonly signal: AbortSignal }
    }> = []
    const outbox: AgentSessionRuntimeEventQueue = {
      ...handles.outbox,
      async awaitInputReceipt(recordId, options) {
        waits.push({ recordId, options })
        return {
          type: 'session.input',
          inputDeliveryId: recordId,
          agentTurnId: 'turn-opencode',
          agentSessionId: 'agent-session-test',
        }
      },
    }
    const controller = new AbortController()
    const result = await callAction(
      opencodeAction,
      baseContext({
        signal: controller.signal,
        with: { prompt: 'run OpenCode', session: 'plan', timeout: 12_345 } as never,
        openCodeRuntime: runtime,
        serverConnection: handles.connection,
        agentSessionRuntimeEventQueue: outbox,
      }),
    )

    expect(result.error).toBeUndefined()
    expect(waits).toHaveLength(1)
    expect(waits[0]).toMatchObject({ recordId: expect.any(String), options: { budgetMs: 12_345 } })
    expect(waits[0]?.options?.signal).toBe(controller.signal)
  })

  it('does not replace a successful Action result when a projected event enqueue fails after input accepted', async () => {
    const { runtime } = buildRuntime({
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: 'session.next.text.delta',
          sessionID: sessionId,
          payload: { textID: 'txt_a', assistantMessageID: 'msg_1', delta: 'alpha' },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    let firstCall = true
    const outbox: AgentSessionRuntimeEventQueue = {
      ready: () => true,
      load: async () => {},
      recover: async () => {},
      async enqueueBeforeExecution(record) {
        handles.records.push(record as RuntimeEventRecord)
      },
      async awaitInputReceipt(recordId) {
        return {
          type: 'session.input',
          inputDeliveryId: recordId,
          agentTurnId: `turn-${recordId}`,
          agentSessionId: 'agent-session-test',
        }
      },
      async enqueueProducedFact() {
        if (firstCall) {
          firstCall = false
          throw new Error('disk full (produced)')
        }
      },
      async enqueueProducedFactBatch() {
        if (firstCall) {
          firstCall = false
          throw new Error('disk full (produced)')
        }
      },
      kick: async () => {},
      stop: async () => {},
      snapshot() {
        return []
      },
    }
    const handles = makeRecordingOutbox()
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventQueue: outbox,
    })
    try {
      const result = await callAction(opencodeAction, context)
      expect(result.error).toBeUndefined()
    } finally {
      errorSpy.mockRestore()
    }
  })

  it('does not replace a runtime failure when produced-fact enqueue also fails', async () => {
    const { runtime } = buildRuntime({
      failPrompt: true,
      failPromptMessage: 'opencode crashed',
      emitDuringPrompt: async (subscription, sessionId) => {
        subscription.emit({
          type: 'session.next.text.delta',
          sessionID: sessionId,
          payload: { textID: 'txt_a', assistantMessageID: 'msg_1', delta: 'alpha' },
        })
        await new Promise((resolve) => setImmediate(resolve))
      },
    })
    await ensureReady(runtime)
    const outbox = makeProducedFactFailureOutbox()
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
      agentSessionRuntimeEventQueue: outbox,
    })
    try {
      const result = await callAction(opencodeAction, context)
      expect(result.error?.code).toBe('turn-failed')
      expect(result.error?.message).toBe('opencode crashed')
    } finally {
      errorSpy.mockRestore()
    }
  })

  it('does not wire a reporter when no outbox is provided', async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const handles = makeRecordingOutbox()
    const context = baseContext({
      openCodeRuntime: runtime,
      serverConnection: handles.connection,
    })
    const result = await callAction(opencodeAction, context)
    expect(result.error).toBeUndefined()
    expect(handles.records).toHaveLength(0)
  })

  it('the observer passes events to the reporter synchronously without awaiting', () => {
    const events: string[] = []
    const observer = {
      onEvent(event: { type: string }) {
        events.push(event.type)
      },
    }
    const observed = [
      { type: 'message.delta', runtimeSessionId: 'x', workDir: '/w', payload: {} },
      { type: 'tool_call.started', runtimeSessionId: 'x', workDir: '/w', payload: {} },
    ]
    for (const event of observed) observer.onEvent?.(event)
    expect(events).toEqual(['message.delta', 'tool_call.started'])
  })
})
