import { describe, expect, it } from 'vitest'
import {
  buildPermissionRejectionUnconfirmed,
  createPermissionRejection,
  scheduleCloseoutWarning,
  scheduleDeadlineInterrupt,
  CODEX_CLOSEOUT_WARNING_LEAD_MS,
  CODEX_CLOSEOUT_WARNING_TEXT,
  CODEX_INTERRUPT_CONFIRMATION_BUDGET_MS,
  type CodexCloseoutTransport,
} from './closeout.js'
import type { CodexClock, CodexDiagnostic } from './types.js'

const THREAD_ID = 'thr_1'
const TURN_ID = 'turn_1'

// ---------------------------------------------------------------------------
// Transport fake
// ---------------------------------------------------------------------------

interface RecordedCall {
  readonly method: string
  readonly params: unknown
  readonly id: number
}

interface DeniedRequest {
  readonly id: string | number
  readonly reason: string
}

interface FakeCloseoutTransport extends CodexCloseoutTransport {
  readonly calls: RecordedCall[]
  readonly denied: DeniedRequest[]
  setResponse(method: string, response: unknown): void
  setError(method: string, error: Error | null): void
  emit(message: unknown): void
  setExited(value: boolean): void
}

function buildTransport(): FakeCloseoutTransport {
  const calls: RecordedCall[] = []
  const denied: DeniedRequest[] = []
  const responses = new Map<string, unknown>()
  const errors = new Map<string, Error>()
  let exited = false
  const listeners = new Set<(message: unknown) => void>()
  const transport: FakeCloseoutTransport = {
    calls,
    denied,
    async send(request) {
      calls.push({ method: request.method, params: request.params, id: request.id })
      const error = errors.get(request.method)
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
      return () => {
        listeners.delete(listener)
      }
    },
    hasExited: () => exited,
    setResponse(method, value) {
      responses.set(method, value)
    },
    setError(method, value) {
      if (value === null) errors.delete(method)
      else errors.set(method, value)
    },
    emit(message) {
      for (const listener of listeners) listener(message)
    },
    setExited(value) {
      exited = value
    },
  }
  return transport
}

// ---------------------------------------------------------------------------
// Clock fake
// ---------------------------------------------------------------------------

interface ScheduledTimer {
  readonly id: number
  readonly delayMs: number
  readonly callback: () => void
}

interface FakeClock extends CodexClock {
  readonly scheduled: ScheduledTimer[]
  advance(ms: number): void
  cancelAll(): void
}

function buildClock(initial = 0): FakeClock {
  let now = initial
  const scheduled: ScheduledTimer[] = []
  let nextId = 1
  return {
    scheduled,
    now: () => now,
    setTimeout(callback, delayMs) {
      const id = nextId++
      scheduled.push({ id, delayMs, callback })
      return id
    },
    clearTimeout(handle) {
      const index = scheduled.findIndex((t) => t.id === handle)
      if (index >= 0) scheduled.splice(index, 1)
    },
    advance(ms) {
      now += ms
      // Fire all timers whose delay has elapsed in arrival order.
      // We process at most one timer per advance step so timers
      // scheduled inside callbacks also fire deterministically.
      let safety = scheduled.length + 1
      while (safety > 0) {
        safety -= 1
        const index = scheduled.findIndex((t) => now >= t.delayMs)
        if (index < 0) break
        const [timer] = scheduled.splice(index, 1)
        timer.callback()
      }
    },
    cancelAll() {
      scheduled.length = 0
    },
  }
}

// ---------------------------------------------------------------------------
// Warning phase
// ---------------------------------------------------------------------------

describe('Codex closeout warning text', () => {
  it('is task-independent and never names a marker or echoes the prompt', () => {
    expect(CODEX_CLOSEOUT_WARNING_TEXT).toBe(
      'Mohist runner deadline approaching; wrap up the current work and return the final answer.',
    )
    // The locked text MUST NOT include any of the marker names used
    // across the closeout protocol (so the warning cannot be
    // confused with task-specific contracts).
    expect(CODEX_CLOSEOUT_WARNING_TEXT).not.toMatch(/marker/i)
    expect(CODEX_CLOSEOUT_WARNING_TEXT).not.toMatch(/reset|rebind|approve|permission|input|turn\/steer/i)
    // The locked text MUST NOT echo a prompt placeholder.
    expect(CODEX_CLOSEOUT_WARNING_TEXT).not.toMatch(/\{prompt\}|<<.+>>|\$\(.+\)/)
  })

  it('uses a 5-minute lead time as the locked default', () => {
    expect(CODEX_CLOSEOUT_WARNING_LEAD_MS).toBe(5 * 60_000)
  })
})

describe('Codex scheduleCloseoutWarning', () => {
  it('submits a turn/steer on the exact active Turn with the locked text at execution start when the deadline is shorter than the lead', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/steer', {
      jsonrpc: '2.0',
      id: 1,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const clock = buildClock(0)
    let nextId = 1
    const handle = scheduleCloseoutWarning({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      // 60 seconds is shorter than the 5-minute lead, so the
      // warning fires at execution start (delay = 0).
      deadlineMs: 60_000,
      clock,
      nextRequestId: () => nextId++,
    })
    expect(handle.scheduled).toBe(true)
    expect(handle.fired).toBe(false)
    expect(handle.effectiveDelayMs).toBe(0)
    clock.advance(0)
    // Allow microtasks for the async turn/steer call to flush.
    await Promise.resolve()
    await Promise.resolve()
    expect(handle.fired).toBe(true)
    expect(transport.calls).toHaveLength(1)
    const call = transport.calls[0]
    expect(call.method).toBe('turn/steer')
    expect(call.params).toEqual({
      threadId: THREAD_ID,
      turnId: TURN_ID,
      input: [{ type: 'text', text: CODEX_CLOSEOUT_WARNING_TEXT }],
    })
    handle.dispose()
  })

  it('submits the warning 5 minutes before the deadline when the deadline is longer than the lead', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/steer', {
      jsonrpc: '2.0',
      id: 1,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const clock = buildClock(0)
    let nextId = 1
    const handle = scheduleCloseoutWarning({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      // 10 minutes total -> warning fires 5 minutes in.
      deadlineMs: 10 * 60_000,
      clock,
      nextRequestId: () => nextId++,
    })
    expect(handle.effectiveDelayMs).toBe(5 * 60_000)
    // Advance only to 4 minutes — warning must NOT have fired.
    clock.advance(4 * 60_000)
    await Promise.resolve()
    expect(transport.calls).toHaveLength(0)
    expect(handle.fired).toBe(false)
    // Advance to the 5-minute mark — warning fires.
    clock.advance(60_000)
    await Promise.resolve()
    await Promise.resolve()
    expect(handle.fired).toBe(true)
    expect(transport.calls).toHaveLength(1)
    expect(transport.calls[0].method).toBe('turn/steer')
    handle.dispose()
  })

  it('fires the warning exactly once per Turn', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/steer', {
      jsonrpc: '2.0',
      id: 1,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const clock = buildClock(0)
    let nextId = 1
    const handle = scheduleCloseoutWarning({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: 60_000,
      clock,
      nextRequestId: () => nextId++,
    })
    clock.advance(0)
    await Promise.resolve()
    await Promise.resolve()
    expect(transport.calls).toHaveLength(1)
    // Re-advancing past the deadline window must NOT re-fire the
    // warning. A second steer would violate the "single warning per
    // Turn" invariant.
    clock.advance(60_000)
    await Promise.resolve()
    await Promise.resolve()
    expect(transport.calls).toHaveLength(1)
    handle.dispose()
  })

  it('does not schedule a warning when the deadline is null', () => {
    const transport = buildTransport()
    const clock = buildClock(0)
    let nextId = 1
    const handle = scheduleCloseoutWarning({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: null,
      clock,
      nextRequestId: () => nextId++,
    })
    expect(handle.scheduled).toBe(true)
    expect(handle.effectiveDelayMs).toBeNull()
    clock.advance(60_000)
    expect(transport.calls).toHaveLength(0)
    handle.dispose()
  })

  it('does not retry a lost steer response and surfaces the failure as a diagnostic only', async () => {
    const transport = buildTransport()
    transport.setError('turn/steer', new Error('transport dropped'))
    const clock = buildClock(0)
    let nextId = 1
    const diagnostics: CodexDiagnostic[] = []
    const handle = scheduleCloseoutWarning({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: 0,
      clock,
      nextRequestId: () => nextId++,
      observer: { onDiagnostic: (d) => diagnostics.push(d) },
    })
    clock.advance(0)
    await Promise.resolve()
    await Promise.resolve()
    expect(handle.fired).toBe(true)
    // The lost steer is recorded as a diagnostic; the runtime does
    // not retry, and no Workflow Approval Point is created.
    expect(diagnostics.some((d) => d.code === 'closeout-warning-lost')).toBe(true)
    expect(transport.calls).toHaveLength(1)
    handle.dispose()
  })

  it('dispose prevents the warning from firing', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/steer', {
      jsonrpc: '2.0',
      id: 1,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const clock = buildClock(0)
    let nextId = 1
    const handle = scheduleCloseoutWarning({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: 60_000,
      clock,
      nextRequestId: () => nextId++,
    })
    handle.dispose()
    clock.advance(60_000)
    await Promise.resolve()
    expect(transport.calls).toHaveLength(0)
    expect(handle.fired).toBe(false)
  })

  it('the warning text is identical across calls and never contains a marker name', () => {
    // The locked text must be stable; a regression that interpolates
    // the prompt or a marker name into the warning is a
    // closeout-protocol regression. We assert the literal value
    // and verify a sentinel does NOT appear.
    expect(CODEX_CLOSEOUT_WARNING_TEXT).not.toContain('marker')
    expect(CODEX_CLOSEOUT_WARNING_TEXT).not.toContain('PROMPT')
    expect(CODEX_CLOSEOUT_WARNING_TEXT).not.toContain('PROMPT_MARKER')
    expect(CODEX_CLOSEOUT_WARNING_TEXT).toBe(
      'Mohist runner deadline approaching; wrap up the current work and return the final answer.',
    )
  })
})

// ---------------------------------------------------------------------------
// Deadline interrupt (Phase 2)
// ---------------------------------------------------------------------------

describe('Codex scheduleDeadlineInterrupt', () => {
  it('fires turn/interrupt on the exact active Turn at the deadline and returns confirmed when the matching terminal event arrives', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/interrupt', {
      jsonrpc: '2.0',
      id: 5,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const clock = buildClock(0)
    let nextId = 1
    const handle = scheduleDeadlineInterrupt({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: 100,
      clock,
      nextRequestId: () => nextId++,
    })
    expect(handle.fired).toBe(false)
    clock.advance(100)
    expect(handle.fired).toBe(true)
    const interruptCall = transport.calls.find((c) => c.method === 'turn/interrupt')
    expect(interruptCall).toBeDefined()
    expect(interruptCall?.params).toEqual({ threadId: THREAD_ID, turnId: TURN_ID })
    // Awaiting confirmation BEFORE the matching terminal event
    // arrives must produce a `confirmed` outcome once we emit the
    // event.
    const confirmationPromise = handle.awaitConfirmation()
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    await expect(confirmationPromise).resolves.toEqual({ status: 'confirmed' })
    handle.dispose()
  })

  it('awaits bounded confirmation and surfaces budget-exhausted when no terminal event arrives', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/interrupt', {
      jsonrpc: '2.0',
      id: 5,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const clock = buildClock(0)
    let nextId = 1
    const handle = scheduleDeadlineInterrupt({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: 100,
      clock,
      nextRequestId: () => nextId++,
    })
    clock.advance(100)
    const confirmationPromise = handle.awaitConfirmation()
    // Advance past the budget. The confirmation outcome is
    // `budget-exhausted`.
    clock.advance(CODEX_INTERRUPT_CONFIRMATION_BUDGET_MS + 1)
    await expect(confirmationPromise).resolves.toEqual({ status: 'budget-exhausted' })
    handle.dispose()
  })

  it('a late completion does not reverse the fixed deadline result', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/interrupt', {
      jsonrpc: '2.0',
      id: 5,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const clock = buildClock(0)
    let nextId = 1
    const handle = scheduleDeadlineInterrupt({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: 100,
      clock,
      nextRequestId: () => nextId++,
    })
    clock.advance(100)
    const confirmation = handle.awaitConfirmation()
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'completed',
    })
    // `completed` is not `interrupted` — confirmation is NOT
    // granted; the deadline result is fixed and a late completion
    // does not reverse it.
    clock.advance(CODEX_INTERRUPT_CONFIRMATION_BUDGET_MS + 1)
    await expect(confirmation).resolves.toEqual({ status: 'budget-exhausted' })
    handle.dispose()
  })

  it('ignores terminal events for different Threads or Turns', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/interrupt', {
      jsonrpc: '2.0',
      id: 5,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const clock = buildClock(0)
    let nextId = 1
    const handle = scheduleDeadlineInterrupt({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: 100,
      clock,
      nextRequestId: () => nextId++,
    })
    clock.advance(100)
    const confirmation = handle.awaitConfirmation()
    transport.emit({
      type: 'turn/completed',
      threadId: 'thr_other',
      turnId: TURN_ID,
      status: 'interrupted',
    })
    transport.emit({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: 'turn_other',
      status: 'interrupted',
    })
    clock.advance(CODEX_INTERRUPT_CONFIRMATION_BUDGET_MS + 1)
    await expect(confirmation).resolves.toEqual({ status: 'budget-exhausted' })
    handle.dispose()
  })
})

// ---------------------------------------------------------------------------
// Permission / user-input rejection
// ---------------------------------------------------------------------------

describe('Codex createPermissionRejection', () => {
  it('denies a server-initiated request addressed to the active Turn and returns permission-required after the matching terminal event', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/interrupt', {
      jsonrpc: '2.0',
      id: 5,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const clock = buildClock(0)
    let nextId = 1
    const rejection = createPermissionRejection({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      clock,
      nextRequestId: () => nextId++,
    })
    expect(rejection.pending).toBe(false)
    const outcome = rejection.observeServerRequest({
      jsonrpc: '2.0',
      id: 99,
      method: 'item/tool/requestApproval',
      params: { threadId: THREAD_ID, turnId: TURN_ID, reason: 'shell command' },
    })
    expect(outcome).toBe('active-turn')
    expect(transport.denied).toEqual([
      {
        id: 99,
        reason: 'Codex headless runtime denies approval / permission / user-input requests',
      },
    ])
    expect(rejection.pending).toBe(true)
    const interruptCall = transport.calls.find((c) => c.method === 'turn/interrupt')
    expect(interruptCall).toBeDefined()
    expect(interruptCall?.params).toEqual({ threadId: THREAD_ID, turnId: TURN_ID })
    // No terminal event has arrived yet, so observeTurnCompleted
    // returns null (the result is NOT yet `permission-required`).
    const early = rejection.observeTurnCompleted({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: 'turn_other',
      status: 'interrupted',
    })
    expect(early).toBeNull()
    // Now the matching terminal event arrives — the result is
    // `permission-required`.
    const result = rejection.observeTurnCompleted({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    expect(result).not.toBeNull()
    if (!result) throw new Error('expected permission-required')
    expect(result).toMatchObject({ ok: false, error: { kind: 'permission-required' } })
    rejection.dispose()
  })

  it('denies a server-initiated request addressed to a different Turn without interrupting the active Turn', () => {
    const transport = buildTransport()
    const clock = buildClock(0)
    let nextId = 1
    const rejection = createPermissionRejection({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      clock,
      nextRequestId: () => nextId++,
    })
    const outcome = rejection.observeServerRequest({
      jsonrpc: '2.0',
      id: 99,
      method: 'item/tool/requestApproval',
      params: { threadId: THREAD_ID, turnId: 'turn_other' },
    })
    expect(outcome).toBe('different-turn')
    expect(transport.denied).toEqual([{ id: 99, reason: 'request addressed to a different active Turn' }])
    expect(rejection.pending).toBe(false)
    expect(transport.calls.some((c) => c.method === 'turn/interrupt')).toBe(false)
    rejection.dispose()
  })

  it('an unconfirmed denial within the bounded confirmation budget surfaces as interruption-unconfirmed and never creates a Workflow Approval Point', async () => {
    const transport = buildTransport()
    transport.setResponse('turn/interrupt', {
      jsonrpc: '2.0',
      id: 5,
      result: { threadId: THREAD_ID, turnId: TURN_ID, accepted: true },
    })
    const clock = buildClock(0)
    let nextId = 1
    const rejection = createPermissionRejection({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      clock,
      nextRequestId: () => nextId++,
    })
    const outcome = rejection.observeServerRequest({
      jsonrpc: '2.0',
      id: 99,
      method: 'item/tool/requestApproval',
      params: { threadId: THREAD_ID, turnId: TURN_ID },
    })
    expect(outcome).toBe('active-turn')
    // The matching terminal event NEVER arrives. Awaiting bounded
    // confirmation returns `budget-exhausted` once the budget
    // elapses.
    const confirmationPromise = rejection.awaitConfirmation()
    // Advance past the bounded confirmation budget.
    clock.advance(CODEX_INTERRUPT_CONFIRMATION_BUDGET_MS + 1)
    const confirmation = await confirmationPromise
    expect(confirmation).toEqual({ status: 'budget-exhausted' })
    // observeTurnCompleted returns null because no denial was
    // confirmed — the AgentSession binding is unchanged.
    const observed = rejection.observeTurnCompleted({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    // The state machine is no longer pending — it timed out — so
    // observeTurnCompleted returns null. The caller is expected
    // to surface `unknown` / `interruption-unconfirmed` and leave
    // the AgentSession binding unchanged.
    expect(observed).toBeNull()
    rejection.dispose()
    // The unconfirmed helper builds the correct surface.
    const unconfirmed = buildPermissionRejectionUnconfirmed({
      threadId: THREAD_ID,
      turnId: TURN_ID,
    })
    expect(unconfirmed).toMatchObject({ ok: false, error: { kind: 'unknown' } })
    if (unconfirmed.ok) throw new Error('expected failure')
    expect(unconfirmed.diagnostics.some((d) => d.code === 'interruption-unconfirmed')).toBe(true)
    // No Workflow Approval Point: no transient approval state was
    // ever created. The protocol-defined denial was the only
    // response; the runtime never opened an approval workflow.
    expect(rejection.pending).toBe(false)
  })

  it('ignores terminal events for different Threads or Turns', () => {
    const transport = buildTransport()
    const clock = buildClock(0)
    let nextId = 1
    const rejection = createPermissionRejection({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      clock,
      nextRequestId: () => nextId++,
    })
    rejection.observeServerRequest({
      jsonrpc: '2.0',
      id: 99,
      method: 'item/tool/requestApproval',
      params: { threadId: THREAD_ID, turnId: TURN_ID },
    })
    const wrongThread = rejection.observeTurnCompleted({
      type: 'turn/completed',
      threadId: 'thr_other',
      turnId: TURN_ID,
      status: 'interrupted',
    })
    const wrongTurn = rejection.observeTurnCompleted({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: 'turn_other',
      status: 'interrupted',
    })
    expect(wrongThread).toBeNull()
    expect(wrongTurn).toBeNull()
    rejection.dispose()
  })

  it('returns null from observeTurnCompleted when no denial is pending', () => {
    const transport = buildTransport()
    const clock = buildClock(0)
    let nextId = 1
    const rejection = createPermissionRejection({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      clock,
      nextRequestId: () => nextId++,
    })
    const result = rejection.observeTurnCompleted({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    expect(result).toBeNull()
    rejection.dispose()
  })

  it('returns null for non-server-initiated messages', () => {
    const transport = buildTransport()
    const clock = buildClock(0)
    let nextId = 1
    const rejection = createPermissionRejection({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      clock,
      nextRequestId: () => nextId++,
    })
    expect(rejection.observeServerRequest({ jsonrpc: '2.0', method: 'item/agentMessage', params: {} })).toBeNull()
    expect(rejection.observeServerRequest(null)).toBeNull()
    expect(rejection.observeServerRequest({ foo: 'bar' })).toBeNull()
    rejection.dispose()
  })

  it('a best-effort interrupt failure does not affect the denial result', async () => {
    const transport = buildTransport()
    transport.setError('turn/interrupt', new Error('stdin closed'))
    const clock = buildClock(0)
    let nextId = 1
    const rejection = createPermissionRejection({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      clock,
      nextRequestId: () => nextId++,
    })
    expect(
      rejection.observeServerRequest({
        jsonrpc: '2.0',
        id: 99,
        method: 'item/tool/requestApproval',
        params: { threadId: THREAD_ID, turnId: TURN_ID },
      }),
    ).toBe('active-turn')
    // The denial was recorded. The interrupt failure is best-
    // effort; once the matching terminal event arrives, the
    // result is still `permission-required`.
    await Promise.resolve()
    await Promise.resolve()
    const result = rejection.observeTurnCompleted({
      type: 'turn/completed',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      status: 'interrupted',
    })
    expect(result).not.toBeNull()
    if (!result) throw new Error('expected permission-required')
    expect(result).toMatchObject({ ok: false, error: { kind: 'permission-required' } })
    rejection.dispose()
  })

  it('dispose clears timers and makes observe* no-ops', () => {
    const transport = buildTransport()
    const clock = buildClock(0)
    let nextId = 1
    const rejection = createPermissionRejection({
      transport,
      threadId: THREAD_ID,
      turnId: TURN_ID,
      clock,
      nextRequestId: () => nextId++,
    })
    rejection.dispose()
    expect(
      rejection.observeServerRequest({
        jsonrpc: '2.0',
        id: 99,
        method: 'item/tool/requestApproval',
        params: { threadId: THREAD_ID, turnId: TURN_ID },
      }),
    ).toBeNull()
    expect(
      rejection.observeTurnCompleted({
        type: 'turn/completed',
        threadId: THREAD_ID,
        turnId: TURN_ID,
        status: 'interrupted',
      }),
    ).toBeNull()
  })
})

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

describe('Codex closeout constants', () => {
  it('exposes a 5-second default interrupt confirmation budget', () => {
    expect(CODEX_INTERRUPT_CONFIRMATION_BUDGET_MS).toBe(5_000)
  })

  it('uses the same 5-minute warning lead documented in the design', () => {
    expect(CODEX_CLOSEOUT_WARNING_LEAD_MS).toBe(5 * 60_000)
  })
})
