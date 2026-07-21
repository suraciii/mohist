import { describe, expect, it } from "vitest"
import { CredentialMasker } from "../src/runtime/task-log.js"
import { PiRuntime, createPiProjector, parseProviderErrorPolicy, type PiClock, type PiSdkFactory, type PiSdkSession, type PiSdkServices } from "../src/runtime/pi/index.js"

class FakeSession implements PiSdkSession {
  readonly sessionFile = "/virtual/sessions/one.jsonl"
  readonly sessionId = "sdk-session"
  messages: Array<{ role: string; content: unknown; stopReason?: string }> = []
  isStreaming = false
  promptCalls: string[] = []
  steerCalls: string[] = []
  abortCalls = 0
  modelCalls: unknown[] = []
  thinkingCalls: string[] = []
  private listeners = new Set<(event: unknown) => void>()
  promptCompletion: (() => void) | null = null

  subscribe(listener: (event: unknown) => void): () => void { this.listeners.add(listener); return () => this.listeners.delete(listener) }
  prompt(text: string): Promise<void> { this.promptCalls.push(text); this.isStreaming = true; return new Promise<void>((resolve) => { this.promptCompletion = () => { this.isStreaming = false; resolve() } }) }
  steer(text: string): Promise<void> { this.steerCalls.push(text); return Promise.resolve() }
  abort(): Promise<void> { this.abortCalls++; this.isStreaming = false; return Promise.resolve() }
  setModel(model: unknown): Promise<void> { this.modelCalls.push(model); return Promise.resolve() }
  setThinkingLevel(level: string): void { this.thinkingCalls.push(level) }
  dispose(): void {}
  emit(event: unknown): void { this.listeners.forEach((listener) => listener(event)) }
  complete(content = "final answer"): void { this.messages.push({ role: "assistant", content }); this.promptCompletion?.() }
}

class FakeClock implements PiClock {
  time = 0
  private next = 0
  private timers = new Map<number, { at: number; callback: () => void }>()
  now = () => this.time
  setTimeout = (callback: () => void, delayMs: number) => { const id = ++this.next; this.timers.set(id, { at: this.time + delayMs, callback }); return id }
  clearTimeout = (handle: unknown) => { this.timers.delete(handle as number) }
  advance(ms: number): void { const target = this.time + ms; while (true) { const due = [...this.timers.entries()].filter(([, timer]) => timer.at <= target).sort((a, b) => a[1].at - b[1].at)[0]; if (!due) break; this.time = due[1].at; this.timers.delete(due[0]); due[1].callback() } this.time = target }
}

function factory(session: FakeSession, catalog = [{ provider: "fake", id: "model", thinkingLevels: ["high"] }]): PiSdkFactory {
  const services: PiSdkServices = {
    catalog: async () => catalog,
    createSession: async () => session,
    openSession: async (path) => { expect(path).toBe(session.sessionFile); return session },
    model: (provider, id) => ({ provider, id }),
    close: async () => {},
  }
  return { create: async () => services }
}

describe("PiRuntime", () => {
  it("gates readiness, permits an empty catalog with a warning, and retries failure", async () => {
    const failing: PiSdkFactory = { create: async () => { throw new Error("credential boundary failed") } }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: failing })
    expect((await runtime.start()).ok).toBe(false)
    expect(runtime.ready()).toBe(false)
    const session = new FakeSession()
    const empty = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session, []) })
    const result = await empty.start()
    expect(result.ok).toBe(true)
    expect(empty.diagnostic()?.severity).toBe("warning")
  })

  it("creates a physical binding and runs a literal prompt with per-turn selection", async () => {
    const session = new FakeSession()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const created = await runtime.createSession({ target: { runtime: "pi", runtimeSessionId: null, workDir: "/workspace" } })
    expect(created.ok).toBe(true)
    const resultPromise = runtime.runTurn({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "/literal prompt", options: { model: "provider/family/model", variant: "high" } }, new AbortController().signal)
    await Promise.resolve()
    expect(session.promptCalls).toEqual(["/literal prompt"])
    expect(session.modelCalls).toEqual([{ provider: "provider", id: "family/model" }])
    expect(session.thinkingCalls).toEqual(["high"])
    session.complete("answer")
    await expect(resultPromise).resolves.toMatchObject({ ok: true, value: { facts: { finalAssistantText: "answer", runtimeSessionId: "/virtual/sessions/one.jsonl" } } })
  })

  it("restores the exact bound path and never replays after a late completion", async () => {
    const session = new FakeSession()
    let opened = ""
    const services = factory(session)
    const original = services.create
    services.create = async (options) => { const value = await original(options); const open = value.openSession; value.openSession = async (path, cwd) => { opened = path; return open(path, cwd) }; return value }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: services })
    await runtime.start()
    const controller = new AbortController()
    const clock = new FakeClock()
    const timedRuntime = new PiRuntime({ agentDir: "/global", sdkFactory: services, clock })
    await timedRuntime.start()
    const resultPromise = timedRuntime.runTurn({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "once", durationMs: 100 }, controller.signal)
    await new Promise<void>((resolve) => setImmediate(resolve))
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["once"])
    clock.advance(100)
    expect(session.abortCalls).toBe(1)
    session.complete("late")
    await expect(resultPromise).resolves.toMatchObject({ ok: false, error: { kind: "deadline-exceeded" } })
    expect(session.promptCalls).toEqual(["once"])
    expect(opened).toBe(session.sessionFile)
  })

  it("steers once at minute 55 and warns immediately for a five minute turn", async () => {
    const session = new FakeSession()
    const clock = new FakeClock()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session), clock })
    await runtime.start()
    const running = runtime.runTurn({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "long", durationMs: 60 * 60_000 }, new AbortController().signal)
    await Promise.resolve()
    clock.advance(55 * 60_000)
    expect(session.steerCalls).toHaveLength(1)
    clock.advance(5 * 60_000)
    session.complete()
    await running
    const shortSession = new FakeSession()
    const shortClock = new FakeClock()
    const shortRuntime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(shortSession), clock: shortClock })
    await shortRuntime.start()
    const short = shortRuntime.runTurn({ target: { runtime: "pi", runtimeSessionId: shortSession.sessionFile, workDir: "/workspace" }, prompt: "short", durationMs: 5 * 60_000 }, new AbortController().signal)
    await Promise.resolve()
    shortClock.advance(0)
    expect(shortSession.steerCalls).toHaveLength(1)
    shortSession.complete()
    await short
  })

  it("does not replace a missing binding and aborts provider exhaustion without leaking credentials", async () => {
    const session = new FakeSession()
    const masker = new CredentialMasker()
    masker.registerSecret("sentinel-provider-key")
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session), masker })
    await runtime.start()
    const controller = new AbortController()
    const events: unknown[] = []
    const turn = runtime.runTurn({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "provider" }, controller.signal, { onEvent: (event) => events.push(event) })
    await new Promise<void>((resolve) => setImmediate(resolve))
    session.emit({ type: "auto_retry_start", id: "retry", attempt: 1, maxAttempts: 5, delayMs: 0, errorMessage: "sentinel-provider-key quota exhausted" })
    const failed = await turn
    expect(failed).toMatchObject({ ok: false, error: { kind: "turn-failed" } })
    expect(JSON.stringify(events)).not.toContain("sentinel-provider-key")
    const missingFactory: PiSdkFactory = {
      create: async () => ({
        catalog: async () => [],
        createSession: async () => session,
        openSession: async () => { throw new Error("session file is corrupt") },
        model: () => ({}),
        close: async () => {},
      }),
    }
    const missing = new PiRuntime({ agentDir: "/global", sdkFactory: missingFactory })
    await missing.start()
    const missingResult = await missing.runTurn({ target: { runtime: "pi", runtimeSessionId: "/virtual/missing.jsonl", workDir: "/workspace" }, prompt: "no replacement" }, new AbortController().signal)
    expect(missingResult).toMatchObject({ ok: false, error: { kind: "missing-session" } })
  })
})

describe("Pi policy and projection", () => {
  it("validates startup policy and appends patterns", () => {
    const parsed = parseProviderErrorPolicy({ MOHIST_PROVIDER_ERROR_PATTERNS: '["sentinel"]', MOHIST_PROVIDER_RETRY_THRESHOLD: "2" })
    expect(parsed.ok).toBe(true)
    if (parsed.ok) { expect(parsed.value.consecutiveRetryThreshold).toBe(2); expect(parsed.value.nonRecoverablePatterns).toHaveLength(14) }
    expect(parseProviderErrorPolicy({ MOHIST_PROVIDER_ERROR_PATTERNS: "[" }).ok).toBe(false)
    expect(parseProviderErrorPolicy({ MOHIST_PROVIDER_RETRY_THRESHOLD: "0" }).ok).toBe(false)
  })

  it("normalizes stable facts, deduplicates callbacks, reconciles final messages, and diagnoses unknown events", () => {
    const projector = createPiProjector("/virtual/session", "/workspace")
    const first = projector.project({ type: "tool_execution_start", toolCallId: "tool-1", toolName: "read" })
    expect(projector.project({ type: "tool_execution_start", toolCallId: "tool-1", toolName: "read" })).toEqual([])
    expect(first[0]?.type).toBe("tool")
    expect(projector.project({ type: "compaction_start", id: "compact-1" })[0]?.payload.phase).toBe("started")
    expect(projector.project({ type: "auto_retry_start", id: "retry-1", attempt: 1, maxAttempts: 5, delayMs: 10, errorMessage: "quota exhausted" })[0]?.type).toBe("provider.retry")
    expect(projector.reconcile([{ role: "assistant", content: "reconciled", usage: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4, thought: 5, cost: { amount: 0.1, currency: "USD" } } }])).toHaveLength(1)
    projector.project({ type: "future_event" })
    expect(projector.diagnostics()).toHaveLength(1)
  })
})
