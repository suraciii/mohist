import { describe, expect, it } from "vitest"
import { CredentialMasker } from "../src/runtime/task-log.js"
import { PiRuntime, createPiProjector, parseProviderErrorPolicy, type PiClock, type PiPromptOptions, type PiSdkFactory, type PiSdkSession, type PiSdkServices } from "../src/runtime/pi/index.js"

class FakeSession implements PiSdkSession {
  sessionFile: string = "/virtual/sessions/one.jsonl"
  readonly sessionId = "sdk-session"
  messages: Array<{ role: string; content: unknown; stopReason?: string }> = []
  isStreaming = false
  promptCalls: string[] = []
  steerCalls: string[] = []
  abortCalls = 0
  compactCalls = 0
  modelCalls: unknown[] = []
  thinkingCalls: string[] = []
  currentModel: unknown = undefined
  currentThinkingLevel: string = "off"
  private listeners = new Set<(event: unknown) => void>()
  private completions: Array<{ resolve: () => void; text: string }> = []
  /** When set to false, the next preflight callback will be invoked with false. Resets to true after use. */
  nextPreflightResult: boolean = true
  /** Captures preflight invocations in order. */
  preflightCalls: Array<{ text: string; success: boolean }> = []

  subscribe(listener: (event: unknown) => void): () => void { this.listeners.add(listener); return () => this.listeners.delete(listener) }
  prompt(text: string, options?: PiPromptOptions): Promise<void> {
    this.promptCalls.push(text)
    const preflight = options?.preflight
    if (preflight) {
      const success = this.nextPreflightResult
      this.nextPreflightResult = true
      this.preflightCalls.push({ text, success })
      queueMicrotask(() => preflight(success))
    }
    return new Promise<void>((resolve) => { this.completions.push({ resolve, text }) })
  }
  steer(text: string): Promise<void> { this.steerCalls.push(text); return Promise.resolve() }
  abort(): Promise<void> { this.abortCalls++; this.isStreaming = false; this.emit({ type: "agent_settled", id: `settled-${this.abortCalls}` }); return Promise.resolve() }
  compact(): Promise<void> { this.compactCalls++; return Promise.resolve() }
  setModel(model: unknown): Promise<void> { this.modelCalls.push(model); this.currentModel = model; return Promise.resolve() }
  setThinkingLevel(level: string): void { this.thinkingCalls.push(level); this.currentThinkingLevel = level }
  getModel(): unknown { return this.currentModel }
  getThinkingLevel(): string { return this.currentThinkingLevel }
  dispose(): void {}
  emit(event: unknown): void { this.listeners.forEach((listener) => listener(event)) }
  complete(content = "final answer"): void {
    this.messages.push({ role: "assistant", content })
    const head = this.completions.shift()
    if (head) { this.isStreaming = this.completions.length > 0; head.resolve() }
  }
  /** Number of currently pending (in-flight) prompts awaiting `complete()`. */
  pendingPrompts(): number { return this.completions.length }
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

function factory(
  session: FakeSession,
  catalog = [{ provider: "fake", id: "model", thinkingLevels: ["high"] }],
  catalogReads: { count: number } = { count: 0 },
): PiSdkFactory {
  const services: PiSdkServices = {
    catalog: async () => { catalogReads.count += 1; return catalog },
    createSession: async () => session,
    openSession: async (path) => { expect(path).toBe(session.sessionFile); return session },
    model: (provider, id) => ({ provider, id }),
    close: async () => {},
  }
  return { create: async () => services }
}

describe("PiRuntime", () => {
  it("gates readiness without reading a model catalog, and retries startup failure", async () => {
    const failing: PiSdkFactory = { create: async () => { throw new Error("credential boundary failed") } }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: failing })
    expect((await runtime.start()).ok).toBe(false)
    expect(runtime.ready()).toBe(false)
    const session = new FakeSession()
    const catalogReads = { count: 0 }
    const empty = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session, [], catalogReads) })
    const result = await empty.start()
    expect(result.ok).toBe(true)
    expect(empty.diagnostic()).toBeNull()
    expect(empty.catalog()).toBeNull()
    expect(catalogReads.count).toBe(0)
  })

  it.each([
    [true, true],
    [false, false],
  ] as const)("reports Pi active-turn state %s from the cached session", async (isStreaming, activeTurn) => {
    const session = new FakeSession()
    session.isStreaming = isStreaming
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    await runtime.createSession({ target: { runtime: "pi", runtimeSessionId: null, workDir: "/workspace" } })

    const result = await runtime.resolveSession({
      target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" },
    })

    expect(result).toMatchObject({ ok: true, value: { activeTurn } })
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
    const turn = runtime.runTurn({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "provider" }, controller.signal, { onEvent: (event) => { events.push(event) } })
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

  it("followup joins an active Pi turn via steer and never starts a new turn", async () => {
    const session = new FakeSession()
    session.isStreaming = true
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const events: unknown[] = []
    const result = await runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "additional guidance" },
      { onEvent: (event) => { events.push(event) } },
    )
    expect(result).toMatchObject({ ok: true, value: { runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace" } })
    expect(session.steerCalls).toEqual(["additional guidance"])
    expect(session.promptCalls).toEqual([])
    expect(events).toEqual([])
  })

  it("followup starts a new turn when idle and resolves once Pi confirms reception", async () => {
    const session = new FakeSession()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const events: unknown[] = []
    const followup = runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "follow me" },
      { onEvent: (event) => { events.push(event) } },
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["follow me"])
    expect(session.preflightCalls).toEqual([{ text: "follow me", success: true }])
    const accepted = await followup
    expect(accepted).toMatchObject({ ok: true, value: { runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace" } })
    session.complete("done")
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.pendingPrompts()).toBe(0)
  })

  it("followup fails when Pi preflight rejects reception and does not retry", async () => {
    const session = new FakeSession()
    session.nextPreflightResult = false
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const events: unknown[] = []
    const followup = runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "ping" },
      { onEvent: (event) => { events.push(event) } },
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    await new Promise<void>((resolve) => setImmediate(resolve))
    const result = await followup
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("turn-failed")
    expect(result.error.diagnostics.some((diag) => diag.code === "preflight-rejected")).toBe(true)
    expect(session.promptCalls).toEqual(["ping"])
    expect(events).toEqual([])
  })

  it("followup reports missing-session with a Reset hint when the bound file is absent", async () => {
    const session = new FakeSession()
    const missingFactory: PiSdkFactory = {
      create: async () => ({
        catalog: async () => [],
        createSession: async () => session,
        openSession: async () => { throw new Error("session file is corrupt") },
        model: () => ({}),
        close: async () => {},
      }),
    }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: missingFactory })
    await runtime.start()
    const result = await runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: "/virtual/missing.jsonl", workDir: "/workspace" }, prompt: "ping" },
    )
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("missing-session")
    expect(result.error.message.toLowerCase()).toContain("reset")
    expect(result.error.diagnostics.some((diag) => diag.code === "missing-session")).toBe(true)
    expect(session.promptCalls).toEqual([])
    expect(session.steerCalls).toEqual([])
  })

  it("followup projects turn events through the observer until prompt resolves", async () => {
    const session = new FakeSession()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const events: unknown[] = []
    const followup = runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "go" },
      { onEvent: (event) => { events.push(event) } },
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    await new Promise<void>((resolve) => setImmediate(resolve))
    await followup
    session.emit({ type: "tool_execution_start", toolCallId: "tool-1", toolName: "read" })
    expect(events.some((event) => (event as { type?: unknown }).type === "tool")).toBe(true)
    session.complete("done")
    await new Promise<void>((resolve) => setImmediate(resolve))
    session.emit({ type: "tool_execution_end", toolCallId: "tool-2", toolName: "read" })
    expect(events.some((event) => (event as { type?: unknown }).type === "tool" && (event as { payload?: { toolCallId?: string } }).payload?.toolCallId === "tool-2")).toBe(false)
  })

  it("cancel reports missing-session with a Reset hint when the bound file is absent", async () => {
    const session = new FakeSession()
    const missingFactory: PiSdkFactory = {
      create: async () => ({
        catalog: async () => [],
        createSession: async () => session,
        openSession: async () => { throw new Error("session file is corrupt") },
        model: () => ({}),
        close: async () => {},
      }),
    }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: missingFactory })
    await runtime.start()
    const result = await runtime.cancel({ target: { runtime: "pi", runtimeSessionId: "/virtual/missing.jsonl", workDir: "/workspace" } })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("missing-session")
    expect(result.error.message.toLowerCase()).toContain("reset")
    expect(session.abortCalls).toBe(0)
  })

  it("keeps the prompt mutex held until an aborted workflow prompt settles", async () => {
    const session = new FakeSession()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const controller = new AbortController()
    const workflow = runtime.runTurn(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "workflow" },
      controller.signal,
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    controller.abort()
    await workflow

    const followup = runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "followup" },
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["workflow"])
    session.complete("workflow stopped")
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["workflow", "followup"])
    await followup
    session.complete("followup done")
    await new Promise<void>((resolve) => setImmediate(resolve))
  })

  it("reports a cached session as missing when reopening its bound file fails", async () => {
    const session = new FakeSession()
    let missing = false
    const services = {
      catalog: async () => [{ provider: "fake", id: "model", thinkingLevels: ["high"] }],
      createSession: async () => session,
      openSession: async (path: string) => {
        expect(path).toBe(session.sessionFile)
        return session
      },
      validateSessionFile: async () => {
        if (missing) throw new Error("session file is gone")
      },
      model: (provider: string, id: string) => ({ provider, id }),
      close: async () => {},
    }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: { create: async () => services } })
    await runtime.start()
    await runtime.followup({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "prime cache" })
    session.complete()
    await new Promise<void>((resolve) => setImmediate(resolve))
    missing = true

    const result = await runtime.followup({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "must reset" })
    expect(result).toMatchObject({ ok: false, error: { kind: "missing-session" } })
    if (result.ok) throw new Error("expected missing-session")
    expect(result.error.message.toLowerCase()).toContain("reset")
  })

  it("serializes a concurrent idle follow-up with an in-flight workflow turn on the same session", async () => {
    const session = new FakeSession()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const events: unknown[] = []
    const workflowTurn = runtime.runTurn(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "workflow" },
      new AbortController().signal,
      { onEvent: (event) => { events.push(event) } },
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["workflow"])
    const followup = runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "followup" },
      { onEvent: (event) => { events.push(event) } },
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["workflow"])
    session.complete("wf done")
    await workflowTurn
    await new Promise<void>((resolve) => setImmediate(resolve))
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["workflow", "followup"])
    await followup
    session.complete("fu done")
    await new Promise<void>((resolve) => setImmediate(resolve))
  })

  it("serializes two concurrent idle follow-ups on the same session", async () => {
    const session = new FakeSession()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const first = runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "first" },
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["first"])
    const second = runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "second" },
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["first"])
    expect(session.promptCalls).toHaveLength(1)
    const firstResult = await first
    expect(firstResult.ok).toBe(true)
    session.complete("first done")
    await new Promise<void>((resolve) => setImmediate(resolve))
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["first", "second"])
    await second
    session.complete("second done")
    await new Promise<void>((resolve) => setImmediate(resolve))
  })

  it("does not rotate the physical Pi Session binding across a Follow-up (busy or idle)", async () => {
    const session = new FakeSession()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    session.isStreaming = true
    const busy = await runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "busy follow" },
    )
    expect(busy).toMatchObject({ ok: true, value: { runtimeSessionId: "/virtual/sessions/one.jsonl" } })
    session.isStreaming = false
    const idle = runtime.followup(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "idle follow" },
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    await new Promise<void>((resolve) => setImmediate(resolve))
    const idleResult = await idle
    expect(idleResult).toMatchObject({ ok: true, value: { runtimeSessionId: "/virtual/sessions/one.jsonl" } })
    session.complete()
    await new Promise<void>((resolve) => setImmediate(resolve))
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
