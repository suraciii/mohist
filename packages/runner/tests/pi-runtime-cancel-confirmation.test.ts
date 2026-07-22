import { describe, expect, it } from "vitest"
import { PiRuntime, type PiClock, type PiSdkFactory, type PiSdkSession } from "../src/runtime/pi/index.js"

class FakeClock implements PiClock {
  private callback: (() => void) | null = null
  now(): number { return 0 }
  setTimeout(callback: () => void): unknown { this.callback = callback; return callback }
  clearTimeout(): void { this.callback = null }
  expire(): void { this.callback?.(); this.callback = null }
}

class FakeSession implements PiSdkSession {
  readonly sessionFile = "/virtual/sessions/one.jsonl"
  readonly sessionId = "session-1"
  readonly messages = []
  isStreaming = true
  abortCalls = 0
  private readonly listeners = new Set<(event: unknown) => void>()
  private resolveSubscription: (() => void) | null = null
  private readonly subscription = new Promise<void>((resolve) => { this.resolveSubscription = resolve })

  subscribe(listener: (event: unknown) => void): () => void { this.listeners.add(listener); this.resolveSubscription?.(); this.resolveSubscription = null; return () => this.listeners.delete(listener) }
  prompt(): Promise<void> { return Promise.resolve() }
  steer(): Promise<void> { return Promise.resolve() }
  abort(): Promise<void> { this.abortCalls++; return Promise.resolve() }
  compact(): Promise<void> { return Promise.resolve() }
  setModel(): Promise<void> { return Promise.resolve() }
  setThinkingLevel(): void {}
  getModel(): unknown { return undefined }
  getThinkingLevel(): string { return "off" }
  dispose(): void {}
  waitForSubscription(): Promise<void> { return this.subscription }
  emit(event: unknown): void { this.listeners.forEach((listener) => listener(event)) }
  settle(): void { this.isStreaming = false; this.emit({ type: "agent_settled" }) }
}

function factory(session: FakeSession): PiSdkFactory {
  return {
    create: async () => ({
      catalog: async () => [],
      createSession: async () => session,
      openSession: async () => session,
      model: () => ({}),
      close: async () => {},
    }),
  }
}

describe("PiRuntime cancel confirmation", () => {
  it("waits for agent_settled after abort resolves", async () => {
    const session = new FakeSession()
    const clock = new FakeClock()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session), clock })
    await runtime.start()

    const cancel = runtime.cancel({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" } })
    await session.waitForSubscription()
    session.settle()

    await expect(cancel).resolves.toMatchObject({ ok: true, value: { cancelled: true, stopConfirmed: true } })
  })

  it("reports an unconfirmed interrupt when Pi never settles before the bounded timeout", async () => {
    const session = new FakeSession()
    session.abort = async () => { session.abortCalls++; session.isStreaming = false; session.emit({ type: "turn_end" }) }
    const clock = new FakeClock()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session), clock })
    await runtime.start()

    const cancel = runtime.cancel({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" } })
    await session.waitForSubscription()
    clock.expire()

    await expect(cancel).resolves.toMatchObject({ ok: true, value: { cancelled: true, stopConfirmed: false } })
  })

  it("does not confirm when agent_settled arrives while streaming remains active", async () => {
    const session = new FakeSession()
    const clock = new FakeClock()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session), clock })
    await runtime.start()

    const cancel = runtime.cancel({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" } })
    await session.waitForSubscription()
    session.emit({ type: "agent_settled" })
    clock.expire()

    await expect(cancel).resolves.toMatchObject({ ok: true, value: { cancelled: true, stopConfirmed: false } })
  })
})
