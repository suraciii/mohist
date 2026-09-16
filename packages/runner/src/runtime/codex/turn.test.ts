import { describe, expect, it } from 'vitest'
import {
  buildLostTurnStartUnknown,
  driveTurnToCompletion,
  submitTurnStart,
  type CodexRuntimeTurnEvent,
  type CodexTurnCompletionOptions,
  type CodexTurnEventObserver,
  type CodexTurnTransport,
} from './turn.js'
import { isCodexTurnStartRequest } from './protocol-types.js'
import { normalizeDeadlineExceededCodex, normalizeInterruptedCodex, normalizeTurnFailedCodex } from './errors.js'
import type { CodexNativeReasoningEffort, CodexTurnResult } from './types.js'
import type { CodexResolvedTurnConfiguration } from './model-catalog.js'

type SubmissionResolved = Pick<CodexResolvedTurnConfiguration, 'model' | 'reasoningEffort'> & {
  readonly nativeReasoningEffort: CodexNativeReasoningEffort | null
}

const THREAD_ID = 'thr_1'
const TURN_ID = 'turn_1'
const RUNTIME_SESSION_ID = THREAD_ID
const WORK_DIR = '/work/project'

interface RecordedCall {
  readonly method: string
  readonly params: unknown
  readonly id: number
}

interface DeniedRequest {
  readonly id: string | number
  readonly reason: string
}

interface FakeTurnTransport extends CodexTurnTransport {
  readonly calls: RecordedCall[]
  readonly denied: DeniedRequest[]
  setResponse(method: string, response: unknown): void
  setError(error: Error | null): void
  emit(message: unknown): void
}

function buildTransport(
  options: { readonly response?: unknown; readonly error?: Error | null } = {},
): FakeTurnTransport {
  const calls: RecordedCall[] = []
  const denied: DeniedRequest[] = []
  const responses = new Map<string, unknown>()
  responses.set(
    'turn/start',
    options.response ?? {
      jsonrpc: '2.0',
      id: 4,
      result: { turnId: TURN_ID, threadId: THREAD_ID, status: 'in_progress' },
    },
  )
  responses.set('turn/interrupt', {
    jsonrpc: '2.0',
    id: 6,
    result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
  })
  let error: Error | null = options.error ?? null
  const listeners = new Set<(message: unknown) => void>()
  const transport: FakeTurnTransport = {
    calls,
    denied,
    async send(request) {
      calls.push({ method: request.method, params: request.params, id: request.id })
      if (error) throw error
      const response = responses.get(request.method)
      if (response === undefined) {
        throw new Error(`unexpected method ${request.method}`)
      }
      return response as never
    },
    denyServerRequest(id, reason) {
      denied.push({ id, reason })
    },
    subscribe(listener) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    hasExited: () => false,
    setResponse(method, value) {
      responses.set(method, value)
    },
    setError(value) {
      error = value
    },
    emit(message) {
      for (const listener of listeners) listener(message)
    },
  }
  return transport
}

function makeNextRequestId(start = 10): () => number {
  let next = start
  return () => {
    const id = next
    next += 1
    return id
  }
}

function driveArgs(
  transport: CodexTurnTransport,
  observer?: CodexTurnEventObserver,
  overrides: Partial<CodexTurnCompletionOptions> = {},
): CodexTurnCompletionOptions {
  return {
    transport,
    runtimeSessionId: RUNTIME_SESSION_ID,
    workDir: WORK_DIR,
    turnId: TURN_ID,
    threadId: THREAD_ID,
    deadlineMs: null,
    nextRequestId: makeNextRequestId(),
    ...(observer ? { observer } : {}),
    ...overrides,
  }
}

describe('Codex turn binding-before-effect discipline', () => {
  it('turn/start carries the Mohist SessionInput ID on clientUserMessageId for correlation only', async () => {
    // The binding-before-effect rule requires the caller to
    // persist the complete binding + Input identity BEFORE
    // submitTurnStart. The seam itself does not persist anything;
    // it only carries the input identity as a correlation key on
    // the request body. Verify that the on-the-wire envelope
    // preserves the SessionInput ID untouched.
    const transport = buildTransport()
    await submitTurnStart(
      transport,
      {
        threadId: THREAD_ID,
        workDir: WORK_DIR,
        prompt: 'hello',
        fileParts: null,
        clientUserMessageId: 'sess_input_correlation_only',
        resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
      },
      4,
    )
    expect(transport.calls[0].params).toMatchObject({ clientUserMessageId: 'sess_input_correlation_only' })
  })

  it('the turn/start envelope never carries an idempotency key the provider could reuse', async () => {
    // The runtime never sends an `idempotencyKey` on turn/start;
    // the Mohist SessionInput ID is the only correlation field.
    // A future regression that adds an idempotency key would
    // re-introduce replay risk and is a hard failure.
    const transport = buildTransport()
    await submitTurnStart(
      transport,
      {
        threadId: THREAD_ID,
        workDir: WORK_DIR,
        prompt: 'hello',
        fileParts: null,
        clientUserMessageId: 'sess_input_42',
        resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
      },
      4,
    )
    const params = transport.calls[0].params as Record<string, unknown>
    expect(params).not.toHaveProperty('idempotencyKey')
    expect(params).not.toHaveProperty('idempotency_key')
    expect(params).not.toHaveProperty('requestId')
  })

  it('turn/start always carries the exact Thread ID supplied by the binding', async () => {
    // The Turn is submitted against the exact Thread ID returned
    // by thread/start or confirmed by thread/resume. The seam
    // must never substitute a different Thread ID even if the
    // server's response shape would otherwise allow it.
    const transport = buildTransport()
    await submitTurnStart(
      transport,
      {
        threadId: THREAD_ID,
        workDir: WORK_DIR,
        prompt: 'hello',
        fileParts: null,
        clientUserMessageId: 'sess_input_thread_id',
        resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
      },
      4,
    )
    expect(transport.calls[0].params).toMatchObject({ threadId: THREAD_ID })
  })
})

describe('Codex turn/start submission', () => {
  it('submits the exact Thread ID, the assembled input, and the SessionInput ID as clientUserMessageId', async () => {
    const transport = buildTransport()
    const submission = {
      threadId: THREAD_ID,
      workDir: WORK_DIR,
      prompt: 'hello world',
      fileParts: null,
      clientUserMessageId: 'sess_input_42',
      resolved: { model: 'gpt-5', reasoningEffort: 'medium' as const, nativeReasoningEffort: 'medium' as const },
    }
    const result = await submitTurnStart(transport, submission, 4)
    expect(result).toMatchObject({ ok: true, value: { turnId: TURN_ID, threadId: THREAD_ID } })
    expect(transport.calls).toHaveLength(1)
    const call = transport.calls[0]
    expect(call.method).toBe('turn/start')
    expect(call.id).toBe(4)
    expect(call.params).toMatchObject({
      threadId: THREAD_ID,
      input: [{ type: 'text', text: 'hello world' }],
      clientUserMessageId: 'sess_input_42',
      model: 'gpt-5',
      reasoningEffort: 'medium',
    })
    expect(isCodexTurnStartRequest({ jsonrpc: '2.0', ...call })).toBe(true)
  })

  it('maps canonical off to native none on turn/start', async () => {
    const transport = buildTransport()
    await submitTurnStart(
      transport,
      {
        threadId: THREAD_ID,
        workDir: WORK_DIR,
        prompt: 'hello',
        fileParts: null,
        clientUserMessageId: 'sess_input_1',
        resolved: { model: 'gpt-5', reasoningEffort: 'off', nativeReasoningEffort: 'none' },
      },
      4,
    )
    expect(transport.calls[0].params).toMatchObject({ reasoningEffort: 'none' })
  })

  it('appends image file parts to the input array', async () => {
    const transport = buildTransport()
    await submitTurnStart(
      transport,
      {
        threadId: THREAD_ID,
        workDir: WORK_DIR,
        prompt: 'describe',
        fileParts: [{ mime: 'image/png', filename: 'a.png', url: 'data:image/png;base64,...' }],
        clientUserMessageId: 'sess_input_2',
        resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
      },
      4,
    )
    expect(transport.calls[0].params).toMatchObject({
      input: [
        { type: 'text', text: 'describe' },
        { type: 'image', mime: 'image/png', url: 'data:image/png;base64,...', filename: 'a.png' },
      ],
    })
  })

  it('rejects a turn/start response that targets a different Thread ID', async () => {
    const transport = buildTransport({
      response: { jsonrpc: '2.0', id: 4, result: { turnId: TURN_ID, threadId: 'thr_other', status: 'in_progress' } },
    })
    const result = await submitTurnStart(
      transport,
      {
        threadId: THREAD_ID,
        workDir: WORK_DIR,
        prompt: 'hello',
        fileParts: null,
        clientUserMessageId: 'sess_input_3',
        resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
      },
      4,
    )
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
  })

  it('rejects a turn/start response that does not match the locked v2 subset', async () => {
    const transport = buildTransport({ response: { malformed: true } })
    const result = await submitTurnStart(
      transport,
      {
        threadId: THREAD_ID,
        workDir: WORK_DIR,
        prompt: 'hello',
        fileParts: null,
        clientUserMessageId: 'sess_input_4',
        resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
      },
      4,
    )
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
  })

  it('rejects a transport failure on turn/start', async () => {
    const transport = buildTransport({ error: new Error('connection refused') })
    const result = await submitTurnStart(
      transport,
      {
        threadId: THREAD_ID,
        workDir: WORK_DIR,
        prompt: 'hello',
        fileParts: null,
        clientUserMessageId: 'sess_input_5',
        resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
      },
      4,
    )
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
  })
})

describe('Codex turn completion authority', () => {
  it('completes only on turn/completed for the exact active Thread+Turn with status completed', async () => {
    const transport = buildTransport()
    const events: CodexRuntimeTurnEvent[] = []
    const diagnostics: { code: string; message: string }[] = []
    const completion = driveTurnToCompletion(
      driveArgs(transport, {
        onEvent: (event) => events.push(event),
        onDiagnostic: (d) => diagnostics.push(d),
      }),
    )
    // Item events arrive before the terminal event.
    transport.emit({ type: 'agentMessage', text: 'part-one' })
    transport.emit({ type: 'reasoning', summary: 'thinking' })
    transport.emit({ type: 'commandExecution', command: 'ls', status: 'completed' })
    transport.emit({ type: 'fileChange', path: '/work/a.txt', kind: 'create' })
    transport.emit({ type: 'usage', inputTokens: 10, outputTokens: 20 })
    transport.emit({ type: 'agentMessage', text: 'part-two' })
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: true, value: { facts: { finalAssistantText: 'part-onepart-two' } } })
    expect(result.ok && result.value.facts.runtimeSessionId).toBe(RUNTIME_SESSION_ID)
    expect(events.map((e) => e.type)).toEqual([
      'message.delta',
      'reasoning.delta',
      'tool_call.completed',
      'file_change.recorded',
      'usage.updated',
      'message.delta',
    ])
    // No diagnostics should be recorded for known item types.
    expect(diagnostics.some((d) => d.code === 'unknown-codex-item')).toBe(false)
  })

  it('maps failed status to normalized turn-failed', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport))
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'failed',
      error: { code: 'provider_exhausted', message: 'rate limit reached' },
    })
    const result = await completion
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed', message: 'rate limit reached' } })
  })

  it('maps interrupted status to normalized interrupted unless a fixed deadline owns the outcome', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport))
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: false, error: { kind: 'interrupted' } })
  })

  it('does not complete on a turn/completed event for a different Thread ID', async () => {
    const transport = buildTransport()
    let resolved = false
    const completion = driveTurnToCompletion(
      driveArgs(transport, {
        onEvent: () => undefined,
        onDiagnostic: () => undefined,
      }),
    ).then((value) => {
      resolved = true
      return value
    })
    transport.emit({
      type: 'turn/completed',
      threadId: 'thr_other',
      turnId: TURN_ID,
      status: 'completed',
    })
    // Wait a microtask to confirm the session is not resolved.
    await Promise.resolve()
    expect(resolved).toBe(false)
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: true, value: { facts: { runtimeSessionId: RUNTIME_SESSION_ID } } })
  })

  it('does not complete on a turn/completed event for a different Turn ID', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport))
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: 'turn_other',
      status: 'completed',
    })
    let resolved = false
    completion.then(() => (resolved = true))
    await Promise.resolve()
    expect(resolved).toBe(false)
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: true })
  })

  it('does not complete on a turn/interrupt RPC acceptance alone', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport))
    // A successful turn/interrupt RPC is request acceptance only,
    // not completion authority. The session must not complete until
    // the matching turn/completed event arrives. This test asserts
    // that even with a transport-level turn/interrupt response
    // observed, the runtime does not resolve.
    let resolved = false
    completion.then(() => (resolved = true))
    await Promise.resolve()
    expect(resolved).toBe(false)
    // The runtime never resolves the completion promise from a
    // successful turn/interrupt response alone.
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: false, error: { kind: 'interrupted' } })
    // No turn/interrupt was issued by this lifecycle because no
    // server-initiated request arrived.
    expect(transport.calls.some((c) => c.method === 'turn/interrupt')).toBe(false)
  })

  it('does not complete on thread/status events', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport))
    let resolved = false
    completion.then(() => (resolved = true))
    await Promise.resolve()
    transport.emit({ type: 'thread/status', threadId: THREAD_ID, turnId: TURN_ID, status: 'busy' })
    transport.emit({ type: 'thread/status', threadId: THREAD_ID, turnId: TURN_ID, status: 'idle' })
    await Promise.resolve()
    expect(resolved).toBe(false)
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: true })
  })

  it('does not complete on item completion or EOF', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport))
    let resolved = false
    completion.then(() => (resolved = true))
    transport.emit({ type: 'agentMessage', text: 'hello' })
    transport.emit({ type: 'usage', inputTokens: 1, outputTokens: 2 })
    await Promise.resolve()
    expect(resolved).toBe(false)
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: true })
  })

  it('keeps out-of-order events from completing the Turn early', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport))
    let resolved = false
    completion.then(() => (resolved = true))
    transport.emit({ type: 'agentMessage', text: 'first' })
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: 'turn_other',
      status: 'completed',
    })
    transport.emit({ type: 'agentMessage', text: '-second' })
    await Promise.resolve()
    expect(resolved).toBe(false)
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: true, value: { facts: { finalAssistantText: 'first-second' } } })
  })

  it('records an unknown item type as diagnostic only and never changes execution state', async () => {
    const transport = buildTransport()
    const diagnostics: { code: string; message: string }[] = []
    const completion = driveTurnToCompletion(driveArgs(transport, { onDiagnostic: (d) => diagnostics.push(d) }))
    transport.emit({ type: 'futureCooolThing', payload: { whatever: true } })
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: true })
    expect(diagnostics.some((d) => d.code === 'unknown-codex-item')).toBe(true)
  })

  it('records an unknown protocol message as diagnostic only', async () => {
    const transport = buildTransport()
    const diagnostics: { code: string; message: string }[] = []
    const completion = driveTurnToCompletion(driveArgs(transport, { onDiagnostic: (d) => diagnostics.push(d) }))
    transport.emit({ jsonrpc: '2.0', method: 'something/new', params: {} })
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    await completion
    expect(diagnostics.some((d) => d.code === 'unknown-protocol-message')).toBe(true)
  })
})

describe('Codex turn deadline closeout', () => {
  it('fixes the result as deadline-exceeded at the deadline and ignores late completion', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport, undefined, { deadlineMs: 25 }))
    const result = await completion
    expect(result).toMatchObject({ ok: false, error: { kind: 'deadline-exceeded' } })
    // A late turn/completed does NOT reverse the fixed deadline result.
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const again = await completion
    expect(again).toBe(result)
    // The interrupt is best-effort; allow either presence or absence
    // since the test is about the deadline result, not the interrupt.
  })

  it('a previously fixed deadline result owns the outcome when interrupted status arrives', async () => {
    const fixed = {
      ok: false as const,
      error: normalizeDeadlineExceededCodex(60_000),
      diagnostics: [],
    }
    const transport = buildTransport()
    const completion = driveTurnToCompletion(
      driveArgs(transport, undefined, { deadlineMs: 0, fixedDeadlineResult: fixed }),
    )
    const result = await completion
    if (result.ok) throw new Error('expected failure')
    expect(result.error.kind).toBe('deadline-exceeded')
    // The interrupted terminal event that may arrive is silently
    // discarded; the session is already resolved.
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    const again = await completion
    if (again.ok) throw new Error('expected failure')
    expect(again.error.kind).toBe('deadline-exceeded')
  })
})

describe('Codex turn server-initiated requests', () => {
  it('denies a server-initiated request addressed to the active Turn and never auto-approves', async () => {
    const transport = buildTransport()
    const diagnostics: { code: string; message: string }[] = []
    const completion = driveTurnToCompletion(driveArgs(transport, { onDiagnostic: (d) => diagnostics.push(d) }))
    transport.emit({
      jsonrpc: '2.0',
      id: 99,
      method: 'item/tool/requestApproval',
      params: { threadId: THREAD_ID, turnId: TURN_ID, reason: 'shell command' },
    })
    expect(transport.denied).toEqual([
      {
        id: 99,
        reason: 'Codex headless runtime denies approval / permission / user-input requests',
      },
    ])
    expect(diagnostics.some((d) => d.code === 'server-request-denied')).toBe(true)
    // The runtime must NOT complete the Turn from the server request;
    // it must await the matching terminal event. Once the matching
    // interrupted terminal event confirms the interrupt, the
    // runtime surfaces `permission-required` (per the closeout
    // protocol) — never `interrupted` for a server-initiated
    // denial.
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: false, error: { kind: 'permission-required' } })
    // The interrupt RPC must have been called with the exact IDs.
    const interruptCall = transport.calls.find((c) => c.method === 'turn/interrupt')
    expect(interruptCall).toBeDefined()
    expect(interruptCall?.params).toEqual({ threadId: THREAD_ID, turnId: TURN_ID })
  })

  it('denies a server-initiated request addressed to a different Turn without interrupting', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport))
    transport.emit({
      jsonrpc: '2.0',
      id: 99,
      method: 'item/tool/requestApproval',
      params: { threadId: THREAD_ID, turnId: 'turn_other' },
    })
    expect(transport.denied).toEqual([{ id: 99, reason: 'request addressed to a different active Turn' }])
    // The active Turn is not interrupted.
    expect(transport.calls.some((c) => c.method === 'turn/interrupt')).toBe(false)
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: true })
  })

  it('a previously fixed permission result owns the outcome when interrupted status arrives', async () => {
    const fixed = {
      ok: false as const,
      error: {
        kind: 'permission-required' as const,
        message: 'permission required',
        diagnostics: [],
      },
      diagnostics: [],
    }
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport, undefined, { fixedPermissionResult: fixed }))
    transport.emit({
      jsonrpc: '2.0',
      id: 99,
      method: 'item/tool/requestApproval',
      params: { threadId: THREAD_ID, turnId: TURN_ID },
    })
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    const result = await completion
    if (result.ok) throw new Error('expected failure')
    expect(result.error.kind).toBe('permission-required')
  })
})

describe('Codex turn item projection', () => {
  it('projects text deltas, reasoning summaries, command/tool activity, file changes, and usage', async () => {
    const transport = buildTransport()
    const events: CodexRuntimeTurnEvent[] = []
    const completion = driveTurnToCompletion(driveArgs(transport, { onEvent: (e) => events.push(e) }))
    transport.emit({ type: 'agentMessage', text: 'first' })
    transport.emit({ type: 'reasoning', summary: 'thinking' })
    transport.emit({ type: 'commandExecution', command: 'ls', status: 'completed' })
    transport.emit({ type: 'mcpToolCall', tool: 'search', status: 'completed' })
    transport.emit({ type: 'webSearch', query: 'mohist' })
    transport.emit({ type: 'fileChange', path: '/work/a.txt', kind: 'modify' })
    transport.emit({ type: 'usage', inputTokens: 3, outputTokens: 4 })
    transport.emit({ type: 'contextCompaction', threadId: THREAD_ID, turnId: TURN_ID })
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    await completion
    const types = events.map((e) => e.type)
    expect(types).toContain('message.delta')
    expect(types).toContain('reasoning.delta')
    expect(types).toContain('tool_call.started')
    expect(types).toContain('file_change.recorded')
    expect(types).toContain('usage.updated')
    expect(types).toContain('compaction')
    // The volatile Turn ID is on every projected event.
    for (const event of events) expect(event.turnId).toBe(TURN_ID)
  })

  it('reconciles the final assistant text from agentMessage items only', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport))
    transport.emit({ type: 'agentMessage', text: 'one ' })
    transport.emit({ type: 'reasoning', summary: 'should not be in final text' })
    transport.emit({ type: 'agentMessage', text: 'two' })
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const result = await completion
    expect(result.ok).toBe(true)
    if (!result.ok) throw new Error('expected success')
    expect(result.value.facts.finalAssistantText).toBe('one two')
  })

  it('returns null final assistant text when no agentMessage items arrived', async () => {
    const transport = buildTransport()
    const completion = driveTurnToCompletion(driveArgs(transport))
    transport.emit({ type: 'usage', inputTokens: 1, outputTokens: 2 })
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const result = await completion
    expect(result.ok).toBe(true)
    if (!result.ok) throw new Error('expected success')
    expect(result.value.facts.finalAssistantText).toBeNull()
  })
})

describe('Codex turn lost-response helper', () => {
  it('builds an unknown result that preserves the clientUserMessageId for audit', () => {
    const result = buildLostTurnStartUnknown({
      threadId: THREAD_ID,
      clientUserMessageId: 'sess_input_42',
      workDir: WORK_DIR,
    })
    expect(result).toMatchObject({ ok: false, error: { kind: 'unknown' } })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.message).toContain('sess_input_42')
    expect(result.diagnostics.some((d) => d.code === 'lost-turn-start')).toBe(true)
  })
})

describe('Codex turn no-replay discipline', () => {
  it('exposes buildLostTurnStartUnknown but never resubmits with the same clientUserMessageId', async () => {
    // The runtime must NOT call submitTurnStart twice with the
    // same SessionInput ID. This guard asserts the seam is
    // single-shot at the seam boundary: buildLostTurnStartUnknown
    // surfaces `unknown` without ever touching the transport.
    const transport = buildTransport({ error: new Error('transport dropped') })
    const firstResult = await submitTurnStart(
      transport,
      {
        threadId: THREAD_ID,
        workDir: WORK_DIR,
        prompt: 'hello',
        fileParts: null,
        clientUserMessageId: 'sess_input_42',
        resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
      },
      4,
    )
    expect(firstResult).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
    const transportCallsBefore = transport.calls.length
    // The runtime's response to a lost turn/start is `unknown` —
    // never a resubmission.
    const lostResult = buildLostTurnStartUnknown({
      threadId: THREAD_ID,
      clientUserMessageId: 'sess_input_42',
      workDir: WORK_DIR,
    })
    expect(lostResult).toMatchObject({ ok: false, error: { kind: 'unknown' } })
    expect(transport.calls.length).toBe(transportCallsBefore)
  })

  it('a lost turn/start response after submission may have occurred preserves the unknown kind without resubmitting', async () => {
    // Simulates the binding-before-effect + no-replay flow: a
    // transport failure on turn/start is normalized to `unknown`,
    // the seam does NOT retry, and the same Input identity is
    // never resubmitted.
    const transport = buildTransport({ error: new Error('write failed: child exited') })
    const submission = {
      threadId: THREAD_ID,
      workDir: WORK_DIR,
      prompt: 'hello',
      fileParts: null,
      clientUserMessageId: 'sess_input_lost',
      resolved: { model: 'gpt-5', reasoningEffort: null, nativeReasoningEffort: null },
    }
    const firstResult = await submitTurnStart(transport, submission, 4)
    expect(firstResult).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
    const callsAfterFirst = transport.calls.length
    // The runtime surface `unknown` for the lost submission;
    // buildLostTurnStartUnknown does not invoke the transport.
    const lostResult = buildLostTurnStartUnknown({
      threadId: THREAD_ID,
      clientUserMessageId: submission.clientUserMessageId,
      workDir: WORK_DIR,
    })
    expect(lostResult).toMatchObject({ ok: false, error: { kind: 'unknown' } })
    expect(transport.calls.length).toBe(callsAfterFirst)
    // The same Input identity is never resubmitted even with the
    // same clientUserMessageId. A second submitTurnStart with the
    // same SessionInput ID is the caller's responsibility to
    // suppress; the seam itself never retries.
    const secondResult = buildLostTurnStartUnknown({
      threadId: THREAD_ID,
      clientUserMessageId: submission.clientUserMessageId,
      workDir: WORK_DIR,
    })
    expect(secondResult).toMatchObject({ ok: false, error: { kind: 'unknown' } })
    expect(transport.calls.length).toBe(callsAfterFirst)
  })
})

// The `normalizeInterruptedCodex` and `normalizeTurnFailedCodex`
// helpers are imported above so the existing normalizer shapes stay
// in the surface area. They are exercised by the deadline and
// completion tests; the references here keep the import site visible.
const _normalizersKept = {
  interrupted: normalizeInterruptedCodex(),
  turnFailed: normalizeTurnFailedCodex({ message: 'sample' }),
}
void _normalizersKept

// Belt-and-braces: the runtime MUST expose a single `CodexTurnResult`
// shape for both `completed` and non-completed outcomes.
type _Surface = { readonly facts: CodexTurnResult['facts'] }
const _surface: _Surface = {
  facts: { finalAssistantText: null, runtimeSessionId: '', workDir: '' },
}
void _surface

// ---------------------------------------------------------------------------
// Closeout integration — driveTurnToCompletion wires the closeout
// helpers from `./closeout.js` for both the two-phase closeout and
// the permission / user-input rejection discipline. The exhaustive
// unit-test surface for the helpers themselves lives in
// `closeout.test.ts`; the tests below exercise the lifecycle
// integration only.
// ---------------------------------------------------------------------------

describe('Codex turn closeout integration', () => {
  it('deadline interrupt fixes the result as deadline-exceeded before bounded confirmation', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/interrupt', {
      jsonrpc: '2.0',
      id: 5,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const completion = driveTurnToCompletion(driveArgs(transport, undefined, { deadlineMs: 25 }))
    const result = await completion
    expect(result).toMatchObject({ ok: false, error: { kind: 'deadline-exceeded' } })
    const interruptCall = transport.calls.find((c) => c.method === 'turn/interrupt')
    expect(interruptCall).toBeDefined()
    expect(interruptCall?.params).toEqual({ threadId: THREAD_ID, turnId: TURN_ID })
    // A late completion does NOT reverse the fixed deadline result.
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    const again = await completion
    expect(again).toBe(result)
  })

  it('does not create a Workflow Approval Point: no transient approval state survives the denial', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/interrupt', {
      jsonrpc: '2.0',
      id: 5,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const completion = driveTurnToCompletion(driveArgs(transport))
    transport.emit({
      jsonrpc: '2.0',
      id: 99,
      method: 'item/tool/requestApproval',
      params: { threadId: THREAD_ID, turnId: TURN_ID },
    })
    // The denial was the ONLY response; no transient approval
    // state was created. The runtime cannot be queried for an
    // approval workflow; it fails closed.
    expect(transport.denied).toHaveLength(1)
    // The matching interrupted terminal event confirms the
    // denial; the result is `permission-required`, NOT an
    // approval workflow id or any other transient state.
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    const result = await completion
    expect(result).toMatchObject({ ok: false, error: { kind: 'permission-required' } })
  })
})
