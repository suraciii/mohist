import { describe, expect, it } from 'vitest'
import { driveTurnToCompletion, type CodexTurnTransport } from './turn.js'

const THREAD_ID = 'thread-abort'
const TURN_ID = 'turn-abort'

function transportWithCalls(calls: string[]): CodexTurnTransport & { emit(message: unknown): void } {
  const listeners = new Set<(message: unknown) => void>()
  return {
    async send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R> {
      calls.push(request.method)
      return { accepted: true } as R
    },
    denyServerRequest() {},
    subscribe(listener) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    emit(message) {
      for (const listener of listeners) listener(message)
    },
  }
}

describe('Codex AgentJob abort closeout', () => {
  it('interrupts an active Turn and waits for its matching terminal event', async () => {
    const calls: string[] = []
    const transport = transportWithCalls(calls)
    const controller = new AbortController()
    const completion = driveTurnToCompletion({
      transport,
      runtimeSessionId: THREAD_ID,
      workDir: '/workspace',
      threadId: THREAD_ID,
      turnId: TURN_ID,
      deadlineMs: null,
      signal: controller.signal,
      nextRequestId: () => 1,
    })

    controller.abort()
    await Promise.resolve()
    expect(calls).toContain('turn/interrupt')

    transport.emit({ type: 'turn/completed', threadId: THREAD_ID, turnId: TURN_ID, status: 'interrupted' })
    await expect(completion).resolves.toMatchObject({ ok: false, error: { kind: 'interrupted' } })
  })
})
