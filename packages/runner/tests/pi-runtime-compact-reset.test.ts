import { describe, expect, it } from "vitest"
import { PiRuntime, type PiSdkFactory, type PiSdkServices, type PiSdkSession, type PiPromptOptions } from "../src/runtime/pi/index.js"

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
  nextCompactOutcome: "ok" | { throw: Error } = "ok"
  compactEvents: unknown[] = []
  private listeners = new Set<(event: unknown) => void>()
  private completions: Array<{ resolve: () => void; text: string }> = []

  subscribe(listener: (event: unknown) => void): () => void { this.listeners.add(listener); return () => this.listeners.delete(listener) }
  prompt(text: string, _options?: PiPromptOptions): Promise<void> {
    this.promptCalls.push(text)
    return new Promise<void>((resolve) => { this.completions.push({ resolve, text }) })
  }
  steer(text: string): Promise<void> { this.steerCalls.push(text); return Promise.resolve() }
  abort(): Promise<void> { this.abortCalls++; this.isStreaming = false; return Promise.resolve() }
  compact(): Promise<void> {
    this.compactCalls++
    const outcome = this.nextCompactOutcome
    this.nextCompactOutcome = "ok"
    if (outcome !== "ok") throw outcome.throw
    for (const event of this.compactEvents) this.emit(event)
    return Promise.resolve()
  }
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
}

function factory(session: FakeSession): PiSdkFactory {
  const services: PiSdkServices = {
    catalog: async () => [{ provider: "fake", id: "model", thinkingLevels: ["high"] }],
    createSession: async () => session,
    openSession: async (path) => { expect(path).toBe(session.sessionFile); return session },
    model: (provider, id) => ({ provider, id }),
    close: async () => {},
  }
  return { create: async () => services }
}

describe("PiRuntime compact channel", () => {
  it("invokes Pi's native compaction, preserves the session identity, and projects the events", async () => {
    const session = new FakeSession()
    session.compactEvents = [
      { type: "compaction_start", id: "compact-1-start" },
      { type: "compaction_end", id: "compact-1-end" },
    ]
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const events: unknown[] = []
    const result = await runtime.compact(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" } },
      { onEvent: (event) => { events.push(event) } },
    )
    expect(result).toMatchObject({ ok: true, value: { runtimeSessionId: "/virtual/sessions/one.jsonl", workDir: "/workspace" } })
    expect(session.compactCalls).toBe(1)
    const compactionEvents = events.filter((event) => (event as { type?: unknown }).type === "compaction_event") as Array<{ payload?: { phase?: string } }>
    expect(compactionEvents.map((event) => event.payload?.phase)).toEqual(["started", "completed"])
  })

  it("returns the unchanged runtimeSessionId so the handler can omit it from SessionCommand", async () => {
    const session = new FakeSession()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const result = await runtime.compact(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" } },
    )
    expect(result.ok).toBe(true)
    if (!result.ok) throw new Error("expected ok")
    expect(result.value.runtimeSessionId).toBe("/virtual/sessions/one.jsonl")
  })

  it("reports conflict when the physical session is still streaming", async () => {
    const session = new FakeSession()
    session.isStreaming = true
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const result = await runtime.compact(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" } },
    )
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("conflict")
    expect(session.compactCalls).toBe(0)
  })

  it("reports a turn failure with no synthetic summary when the native call throws", async () => {
    const session = new FakeSession()
    session.nextCompactOutcome = { throw: new Error("provider exploded") }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const result = await runtime.compact(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" } },
    )
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("turn-failed")
    expect(result.error.diagnostics.some((diag) => diag.code === "compact-failed")).toBe(true)
    expect(session.messages).toEqual([])
  })

  it("reports missing-session with a Reset hint when the bound file is absent", async () => {
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
    const result = await runtime.compact(
      { target: { runtime: "pi", runtimeSessionId: "/virtual/missing.jsonl", workDir: "/workspace" } },
    )
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("missing-session")
    expect(result.error.message.toLowerCase()).toContain("reset")
    expect(session.compactCalls).toBe(0)
  })

  it("serializes with an in-flight workflow turn on the same session", async () => {
    const session = new FakeSession()
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session) })
    await runtime.start()
    const workflow = runtime.runTurn(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "workflow" },
      new AbortController().signal,
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.promptCalls).toEqual(["workflow"])
    const compact = runtime.compact(
      { target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" } },
    )
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.compactCalls).toBe(0)
    session.complete("wf done")
    await workflow
    await new Promise<void>((resolve) => setImmediate(resolve))
    expect(session.compactCalls).toBe(1)
    await compact
  })
})

describe("PiRuntime reset channel", () => {
  it("creates a new empty Pi session in the same workDir, carries model/thinking, and returns a different runtimeSessionId", async () => {
    const prior = new FakeSession()
    prior.currentModel = { provider: "fake", id: "carried-model" }
    prior.currentThinkingLevel = "high"
    const next = new FakeSession()
    next.sessionFile = "/virtual/sessions/two.jsonl"
    const services: PiSdkServices = {
      catalog: async () => [{ provider: "fake", id: "model", thinkingLevels: ["high"] }],
      createSession: async () => next,
      openSession: async (path) => {
        expect(path).toBe(prior.sessionFile)
        return prior
      },
      model: (provider, id) => ({ provider, id }),
      close: async () => {},
    }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: { create: async () => services } })
    await runtime.start()
    const result = await runtime.reset(
      { target: { runtime: "pi", runtimeSessionId: prior.sessionFile, workDir: "/workspace" } },
    )
    expect(result.ok).toBe(true)
    if (!result.ok) throw new Error("expected ok")
    expect(result.value.runtimeSessionId).toBe("/virtual/sessions/two.jsonl")
    expect(result.value.runtimeSessionId).not.toBe(prior.sessionFile)
    expect(next.modelCalls).toEqual([{ provider: "fake", id: "carried-model" }])
    expect(next.thinkingCalls).toEqual(["high"])
  })

  it("succeeds and skips carry-over when the prior session file is missing", async () => {
    const next = new FakeSession()
    next.sessionFile = "/virtual/sessions/next.jsonl"
    const services: PiSdkServices = {
      catalog: async () => [{ provider: "fake", id: "model", thinkingLevels: ["high"] }],
      createSession: async () => next,
      openSession: async () => { throw new Error("session file is corrupt") },
      model: (provider, id) => ({ provider, id }),
      close: async () => {},
    }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: { create: async () => services } })
    await runtime.start()
    const result = await runtime.reset(
      { target: { runtime: "pi", runtimeSessionId: "/virtual/missing.jsonl", workDir: "/workspace" } },
    )
    expect(result.ok).toBe(true)
    if (!result.ok) throw new Error("expected ok")
    expect(result.value.runtimeSessionId).toBe("/virtual/sessions/next.jsonl")
    expect(result.value.runtimeSessionId).not.toBe("/virtual/missing.jsonl")
    expect(next.modelCalls).toEqual([])
    expect(next.thinkingCalls).toEqual([])
  })

  it("leaves the prior session file on disk and only disposes the cached handle", async () => {
    const prior = new FakeSession()
    prior.currentModel = { provider: "fake", id: "keep-me" }
    prior.currentThinkingLevel = "medium"
    let priorDisposed = false
    const originalDispose = prior.dispose.bind(prior)
    prior.dispose = () => { priorDisposed = true; originalDispose() }
    const next = new FakeSession()
    next.sessionFile = "/virtual/sessions/next.jsonl"
    const services: PiSdkServices = {
      catalog: async () => [{ provider: "fake", id: "model", thinkingLevels: ["medium"] }],
      createSession: async () => next,
      openSession: async (path) => { expect(path).toBe(prior.sessionFile); return prior },
      model: (provider, id) => ({ provider, id }),
      close: async () => {},
    }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: { create: async () => services } })
    await runtime.start()
    const result = await runtime.reset(
      { target: { runtime: "pi", runtimeSessionId: prior.sessionFile, workDir: "/workspace" } },
    )
    expect(result.ok).toBe(true)
    expect(priorDisposed).toBe(true)
    expect(prior.sessionFile).toBe("/virtual/sessions/one.jsonl")
    expect(next.modelCalls).toEqual([{ provider: "fake", id: "keep-me" }])
    expect(next.thinkingCalls).toEqual(["medium"])
  })

  it("returns a non-empty runtimeSessionId that differs from the request id", async () => {
    const prior = new FakeSession()
    const next = new FakeSession()
    next.sessionFile = "/virtual/sessions/child.jsonl"
    const services: PiSdkServices = {
      catalog: async () => [{ provider: "fake", id: "model", thinkingLevels: [] }],
      createSession: async () => next,
      openSession: async (path) => { expect(path).toBe(prior.sessionFile); return prior },
      model: (provider, id) => ({ provider, id }),
      close: async () => {},
    }
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: { create: async () => services } })
    await runtime.start()
    const request: { target: { runtime: "pi"; runtimeSessionId: string | null; workDir: string } } = { target: { runtime: "pi", runtimeSessionId: prior.sessionFile, workDir: "/workspace" } }
    const result = await runtime.reset(request)
    expect(result.ok).toBe(true)
    if (!result.ok) throw new Error("expected ok")
    expect(result.value.runtimeSessionId.length).toBeGreaterThan(0)
    expect(result.value.runtimeSessionId).not.toBe(request.target.runtimeSessionId)
  })
})
