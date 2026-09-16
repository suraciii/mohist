import { describe, expect, it } from 'vitest'
import {
  buildCodexThreadResumeRequest,
  buildCodexThreadStartRequest,
  isStructuredThreadNotFound,
  resumeThread,
  startThread,
  type CodexThreadTransport,
} from './thread.js'
import {
  CODEX_APPROVAL_POLICY,
  CODEX_SANDBOX_POLICY,
  isCodexThreadResumeRequest,
  isCodexThreadStartRequest,
} from './protocol-types.js'

interface RecordedCall {
  readonly method: string
  readonly params: unknown
  readonly id: number
}

interface FakeTransport extends CodexThreadTransport {
  readonly calls: RecordedCall[]
  setResponse(response: unknown): void
  setError(error: Error): void
  setExited(value: boolean): void
}

function buildTransport(
  options: { readonly response?: unknown; readonly error?: Error; readonly exited?: boolean } = {},
): FakeTransport {
  const calls: RecordedCall[] = []
  let response: unknown = options.response ?? {
    jsonrpc: '2.0',
    id: 1,
    result: { threadId: 'thr_1', cwd: '/work', model: 'gpt-5', reasoningEffort: 'medium' },
  }
  let error: Error | null = options.error ?? null
  let exited = options.exited ?? false
  return {
    calls,
    async send(request) {
      calls.push({ method: request.method, params: request.params, id: request.id })
      if (error) throw error
      return response as never
    },
    hasExited: () => exited,
    setResponse(value: unknown) {
      response = value
    },
    setError(value: Error) {
      error = value
    },
    setExited(value: boolean) {
      exited = value
    },
  } as FakeTransport
}

const WORK_DIR = '/work'

describe('Codex thread/start', () => {
  it('sends the immutable working directory, locked trust settings, model, and effort', async () => {
    const transport = buildTransport()
    const result = await startThread(transport, { workDir: WORK_DIR, model: 'gpt-5', reasoningEffort: 'medium' }, 1)
    expect(result).toMatchObject({ ok: true, value: { threadId: 'thr_1', workDir: WORK_DIR } })
    expect(transport.calls).toHaveLength(1)
    const call = transport.calls[0]
    expect(call.method).toBe('thread/start')
    expect(call.id).toBe(1)
    expect(call.params).toMatchObject({
      cwd: WORK_DIR,
      approvalPolicy: CODEX_APPROVAL_POLICY,
      sandbox: CODEX_SANDBOX_POLICY,
      persistHistory: true,
      model: 'gpt-5',
      reasoningEffort: 'medium',
    })
    expect(call.params).not.toHaveProperty('ephemeral')
    expect(call.params).not.toHaveProperty('clientUserMessageId')
    // The locked predicate must accept the outbound envelope.
    expect(isCodexThreadStartRequest({ jsonrpc: '2.0', id: call.id, method: call.method, params: call.params })).toBe(
      true,
    )
  })

  it('omits model and reasoning effort when the resolved turn config is null', async () => {
    const transport = buildTransport()
    const result = await startThread(transport, { workDir: WORK_DIR, model: null, reasoningEffort: null }, 2)
    expect(result).toMatchObject({ ok: true })
    expect(transport.calls[0].params).toMatchObject({
      cwd: WORK_DIR,
      approvalPolicy: 'never',
      sandbox: 'danger-full-access',
      persistHistory: true,
    })
    expect(transport.calls[0].params).not.toHaveProperty('model')
    expect(transport.calls[0].params).not.toHaveProperty('reasoningEffort')
  })

  it('maps canonical off to native none for thread/start', async () => {
    const transport = buildTransport()
    await startThread(transport, { workDir: WORK_DIR, model: 'gpt-5', reasoningEffort: 'off' }, 1)
    expect(transport.calls[0].params).toMatchObject({ reasoningEffort: 'none' })
  })

  it('surfaces a warning diagnostic when the server returns a different cwd', async () => {
    const transport = buildTransport({
      response: {
        jsonrpc: '2.0',
        id: 1,
        result: { threadId: 'thr_1', cwd: '/somewhere/else', model: 'gpt-5' },
      },
    })
    const result = await startThread(transport, { workDir: WORK_DIR, model: 'gpt-5', reasoningEffort: null }, 1)
    expect(result).toMatchObject({ ok: true, value: { threadId: 'thr_1' } })
    if (!result.ok) throw new Error('expected success')
    expect(result.diagnostics.some((d) => d.code === 'thread-cwd-mismatch')).toBe(true)
  })

  it('returns turn-failed when the response shape is outside the locked v2 subset', async () => {
    const transport = buildTransport({ response: { not: 'a thread/start result' } })
    const result = await startThread(transport, { workDir: WORK_DIR, model: null, reasoningEffort: null }, 1)
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
  })

  it('returns turn-failed when the transport rejects', async () => {
    const transport = buildTransport({ error: new Error('connection refused') })
    const result = await startThread(transport, { workDir: WORK_DIR, model: null, reasoningEffort: null }, 1)
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics.some((d) => d.message.includes('transport failed'))).toBe(true)
  })

  it('returns turn-failed when the child has exited before the response was observed', async () => {
    const transport = buildTransport({ exited: true })
    const result = await startThread(transport, { workDir: WORK_DIR, model: null, reasoningEffort: null }, 1)
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics.some((d) => d.message.includes('child exited'))).toBe(true)
  })

  it('rejects a non-positive request id', async () => {
    const transport = buildTransport()
    await expect(startThread(transport, { workDir: WORK_DIR, model: null, reasoningEffort: null }, 0)).rejects.toThrow(
      /positive safe integer/,
    )
    expect(transport.calls).toHaveLength(0)
  })
})

describe('Codex thread binding-before-effect discipline', () => {
  it('thread/start sends no caller-supplied idempotency key (binding-before-effect requires caller-controlled persistence)', async () => {
    const transport = buildTransport()
    await startThread(transport, { workDir: WORK_DIR, model: 'gpt-5', reasoningEffort: 'medium' }, 1)
    const params = transport.calls[0].params as Record<string, unknown>
    // The runtime never sends an idempotency key on thread/start.
    // The binding-before-effect rule requires the caller (the
    // Workflow aggregate) to gate persistence, not the runtime.
    expect(params).not.toHaveProperty('idempotencyKey')
    expect(params).not.toHaveProperty('idempotency_key')
    expect(params).not.toHaveProperty('clientUserMessageId')
  })

  it('thread/start envelope always carries the immutable working directory', async () => {
    const transport = buildTransport()
    await startThread(transport, { workDir: WORK_DIR, model: 'gpt-5', reasoningEffort: null }, 1)
    expect(transport.calls[0].params).toMatchObject({ cwd: WORK_DIR })
  })

  it('thread/start envelope always carries the locked approval policy and sandbox', async () => {
    const transport = buildTransport()
    await startThread(transport, { workDir: WORK_DIR, model: 'gpt-5', reasoningEffort: null }, 1)
    expect(transport.calls[0].params).toMatchObject({
      approvalPolicy: 'never',
      sandbox: 'danger-full-access',
    })
  })

  it('thread/start envelope always carries non-ephemeral history (persistHistory: true)', async () => {
    const transport = buildTransport()
    await startThread(transport, { workDir: WORK_DIR, model: 'gpt-5', reasoningEffort: null }, 1)
    expect(transport.calls[0].params).toMatchObject({ persistHistory: true })
  })

  it('thread/start never sets ephemeral=true (transcripts must survive across requests)', async () => {
    const transport = buildTransport()
    await startThread(transport, { workDir: WORK_DIR, model: 'gpt-5', reasoningEffort: null }, 1)
    const params = transport.calls[0].params as Record<string, unknown>
    expect(params.ephemeral).not.toBe(true)
  })
})

describe('Codex thread/resume', () => {
  it('sends excludeTurns: true on the bound Runner', async () => {
    const transport = buildTransport({
      response: {
        jsonrpc: '2.0',
        id: 2,
        result: { threadId: 'thr_1', cwd: WORK_DIR, model: 'gpt-5' },
      },
    })
    const result = await resumeThread(transport, 'thr_1', WORK_DIR, 2)
    expect(result).toMatchObject({ ok: true, value: { threadId: 'thr_1', workDir: WORK_DIR } })
    expect(transport.calls).toHaveLength(1)
    expect(transport.calls[0].params).toMatchObject({
      threadId: 'thr_1',
      cwd: WORK_DIR,
      excludeTurns: true,
    })
    expect(
      isCodexThreadResumeRequest({
        jsonrpc: '2.0',
        id: transport.calls[0].id,
        method: transport.calls[0].method,
        params: transport.calls[0].params,
      }),
    ).toBe(true)
  })

  it('recognizes structured thread_not_found evidence as definitely-missing', async () => {
    const transport = buildTransport({
      response: {
        jsonrpc: '2.0',
        id: 2,
        error: { code: 'thread_not_found', message: 'Thread thr_1 is no longer present' },
      },
    })
    const result = await resumeThread(transport, 'thr_1', WORK_DIR, 2)
    expect(result).toMatchObject({ ok: false, error: { kind: 'missing-session' } })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics.some((d) => d.code === 'thread-not-found')).toBe(true)
  })

  it('recognizes structured thread_not_found from a numeric error code', async () => {
    const transport = buildTransport({
      response: {
        jsonrpc: '2.0',
        id: 2,
        error: { code: 404, message: 'thread thr_1 not found' },
      },
    })
    const result = await resumeThread(transport, 'thr_1', WORK_DIR, 2)
    expect(result).toMatchObject({ ok: false, error: { kind: 'missing-session' } })
  })

  it('recognizes structured thread_not_found from data.code', async () => {
    const transport = buildTransport({
      response: {
        jsonrpc: '2.0',
        id: 2,
        error: { code: 'not_found', message: 'gone', data: { code: 'thread_not_found' } },
      },
    })
    const result = await resumeThread(transport, 'thr_1', WORK_DIR, 2)
    expect(result).toMatchObject({ ok: false, error: { kind: 'missing-session' } })
  })

  it('keeps transport / timeout / 5xx / auth / permission failures as turn-failed (not missing-session)', async () => {
    const transport = buildTransport({ error: new Error('connection reset by peer') })
    const result = await resumeThread(transport, 'thr_1', WORK_DIR, 2)
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
  })

  it('keeps protocol mismatch as turn-failed (not missing-session)', async () => {
    const transport = buildTransport({ response: { malformed: true } })
    const result = await resumeThread(transport, 'thr_1', WORK_DIR, 2)
    expect(result).toMatchObject({ ok: false, error: { kind: 'turn-failed' } })
  })

  it('records a thread-id mismatch diagnostic when the server returns a different thread id', async () => {
    const transport = buildTransport({
      response: {
        jsonrpc: '2.0',
        id: 2,
        result: { threadId: 'thr_other', cwd: WORK_DIR },
      },
    })
    const result = await resumeThread(transport, 'thr_1', WORK_DIR, 2)
    expect(result).toMatchObject({ ok: true, value: { threadId: 'thr_other', workDir: WORK_DIR } })
    if (!result.ok) throw new Error('expected success')
    expect(result.diagnostics.some((d) => d.code === 'thread-id-mismatch')).toBe(true)
  })
})

describe('isStructuredThreadNotFound', () => {
  it('returns false for null envelopes', () => {
    expect(isStructuredThreadNotFound(null)).toBe(false)
  })

  it('returns false for empty objects', () => {
    expect(isStructuredThreadNotFound({})).toBe(false)
  })

  it('returns false for envelopes without an error field', () => {
    expect(isStructuredThreadNotFound({ jsonrpc: '2.0', result: { ok: true } })).toBe(false)
  })

  it('matches when the canonical error code is thread_not_found', () => {
    expect(isStructuredThreadNotFound({ error: { code: 'thread_not_found', message: 'gone' } })).toBe(true)
  })

  it('matches when the canonical message wording is used', () => {
    expect(isStructuredThreadNotFound({ error: { code: -1, message: 'thread not found on disk' } })).toBe(true)
  })

  it('does not match transport / timeout / 5xx wording', () => {
    expect(isStructuredThreadNotFound({ error: { code: 500, message: 'upstream provider 500' } })).toBe(false)
    expect(isStructuredThreadNotFound({ error: { code: 408, message: 'request timeout' } })).toBe(false)
  })
})

describe('Codex thread envelope builders', () => {
  it('builds a thread/start envelope that passes the locked predicate', () => {
    const envelope = buildCodexThreadStartRequest({
      workDir: WORK_DIR,
      model: 'gpt-5',
      reasoningEffort: 'medium',
      id: 1,
    })
    expect(envelope.method).toBe('thread/start')
    expect(envelope.id).toBe(1)
    expect(envelope.params).toMatchObject({
      cwd: WORK_DIR,
      approvalPolicy: 'never',
      sandbox: 'danger-full-access',
      persistHistory: true,
      model: 'gpt-5',
      reasoningEffort: 'medium',
    })
    expect(isCodexThreadStartRequest({ jsonrpc: '2.0', ...envelope })).toBe(true)
  })

  it('builds a thread/resume envelope with excludeTurns: true that passes the locked predicate', () => {
    const envelope = buildCodexThreadResumeRequest({
      threadId: 'thr_1',
      workDir: WORK_DIR,
      id: 2,
    })
    expect(envelope.method).toBe('thread/resume')
    expect(envelope.id).toBe(2)
    expect(envelope.params).toMatchObject({
      threadId: 'thr_1',
      cwd: WORK_DIR,
      excludeTurns: true,
    })
    expect(isCodexThreadResumeRequest({ jsonrpc: '2.0', ...envelope })).toBe(true)
  })

  it('rejects a non-positive request id in the envelope builders', () => {
    expect(() => buildCodexThreadStartRequest({ workDir: WORK_DIR, id: 0 })).toThrow(/positive safe integer/)
    expect(() => buildCodexThreadResumeRequest({ threadId: 'thr_1', workDir: WORK_DIR, id: -1 })).toThrow(
      /positive safe integer/,
    )
  })
})

// Exhaustive: the binding-before-effect rule for thread/start is that the
// caller MUST persist the complete AgentSession binding before calling
// turn/start. The thread lifecycle never calls turn/start itself — the
// surface stays narrow on purpose. This guard exists so a future change
// cannot silently introduce a turn/start call into the thread seam.
const startThreadImpl = startThread.toString()
const resumeThreadImpl = resumeThread.toString()
expect(startThreadImpl).not.toMatch(/turn\/start/)
expect(resumeThreadImpl).not.toMatch(/turn\/start/)
