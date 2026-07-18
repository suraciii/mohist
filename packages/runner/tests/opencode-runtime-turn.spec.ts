import { afterEach, describe, expect, it, vi } from "vitest"
import {
  OpenCodeRuntime,
  type RuntimeProviderErrorPolicy,
} from "../src/runtime/opencode/index.js"
import type { OpenCodeRuntimeDeps } from "../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import type { RuntimeModelCatalog } from "../src/runtime/opencode/types.js"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"

const DEFAULT_SESSION_ID = "ses_/tmp/projA"

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

interface FakeClientHandles {
  health: ReturnType<typeof vi.fn>
  sessionCreate: ReturnType<typeof vi.fn>
  sessionPrompt: ReturnType<typeof vi.fn>
  sessionPromptAsync: ReturnType<typeof vi.fn>
  sessionAbort: ReturnType<typeof vi.fn>
  sessionMessages: ReturnType<typeof vi.fn>
  sessionGet: ReturnType<typeof vi.fn>
  sessionStatus: ReturnType<typeof vi.fn>
}

interface BuildArgs {
  failHealth?: boolean
  failCreate?: boolean
  failPrompt?: boolean
  failPromptAsync?: boolean
  promptResult?: unknown
  promptAsyncResult?: unknown
  createId?: (params: { directory?: string }) => string
  policy?: RuntimeProviderErrorPolicy
  rebuildDelayMs?: number
}

interface BuildResult {
  deps: OpenCodeRuntimeDeps
  subscription: FakeSubscription
  client: FakeClientHandles
  server: OpencodeServerHandle
}

function buildRuntime(args: BuildArgs = {}): BuildResult {
  const subscription = new FakeSubscription()
  const closed = { value: false }
  const catalog: RuntimeModelCatalog = {
    models: [
      { providerID: "openai", modelID: "gpt-5", variants: ["low", "high"] },
      { providerID: "anthropic", modelID: "claude-sonnet-4", variants: [] },
    ],
    fetchedAt: 0,
  }
  const health = vi.fn(async () => ({ data: { ok: true } }))
  if (args.failHealth) health.mockRejectedValueOnce(new Error("health boom"))

  const sessionCreate = vi.fn(async (params: { directory?: string; model?: unknown }) => {
    if (args.failCreate) throw new Error("create boom")
    const id = args.createId ? args.createId(params) : DEFAULT_SESSION_ID
    return { data: { id } }
  })

  const sessionPrompt = vi.fn(async (_params: { sessionID: string; directory?: string; parts?: unknown }) => {
    if (args.failPrompt) throw new Error("prompt boom")
    if (args.promptResult !== undefined) return args.promptResult
    return {
      data: {
        info: { id: "msg_1", sessionID: "ses_1", role: "assistant" },
        parts: [{ type: "text", text: "hello from opencode" }],
      },
    }
  })

  const sessionPromptAsync = vi.fn(async (_params: { sessionID: string; directory?: string; parts?: unknown }) => {
    if (args.failPromptAsync) throw new Error("promptAsync boom")
    if (args.promptAsyncResult !== undefined) return args.promptAsyncResult
    return { data: true }
  })

  const sessionAbort = vi.fn(async (_params: { sessionID: string; directory?: string }) => ({ data: true }))

  const sessionMessages = vi.fn(async () => ({ data: [] }))
  const sessionGet = vi.fn(async () => ({ data: { id: "ses_1" } }))
  const sessionStatus = vi.fn(async () => ({ data: {} }))

  const clientProxy = {
    global: { health, event: vi.fn(async () => ({ stream: (async function* () { void subscription })() })) },
    v2: { provider: { list: vi.fn(async () => ({ data: { data: [] } })) }, model: { list: vi.fn(async () => ({ data: { data: catalog.models.map((m) => ({ id: m.modelID, providerID: m.providerID, variants: m.variants.map((id) => ({ id })) })) } })) } },
    session: {
      create: sessionCreate,
      prompt: sessionPrompt,
      promptAsync: sessionPromptAsync,
      abort: sessionAbort,
      messages: sessionMessages,
      get: sessionGet,
      status: sessionStatus,
    },
  }
  const server: OpencodeServerHandle = {
    url: "http://fake",
    directory: "/tmp/work",
    client: clientProxy as unknown as OpencodeClient,
    async close() {
      closed.value = true
    },
  }
  const client: FakeClientHandles = {
    health,
    sessionCreate,
    sessionPrompt,
    sessionPromptAsync,
    sessionAbort,
    sessionMessages,
    sessionGet,
    sessionStatus,
  }
  const deps: OpenCodeRuntimeDeps = {
    directory: "/tmp/work",
    serverFactory: async () => server,
    catalogFactory: () => ({ async list() { return catalog } }),
    eventSubscriptionFactory: () => subscription,
    ...(args.policy ? { providerErrorPolicy: args.policy } : {}),
    ...(args.rebuildDelayMs !== undefined ? { rebuildDelayMs: args.rebuildDelayMs } : {}),
  }
  return { deps, subscription, client, server }
}

afterEach(() => {
  vi.useRealTimers()
})

describe("OpenCodeRuntime.runTurn — happy path + turn fact", () => {
  it("Resolves a fresh Session, runs the awaited prompt, and populates finalAssistantText", async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do the work",
      options: {
        model: { providerID: "openai", modelID: "gpt-5" },
        variant: "high",
        unknownKeys: undefined,
      },
    }, new AbortController().signal)

    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value.facts.finalAssistantText).toBe("hello from opencode")
    expect(result.value.facts.runtimeSessionId).toMatch(/^ses_/)
    expect(result.value.facts.workDir).toBe("/tmp/projA")
    expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
    expect(client.sessionAbort).not.toHaveBeenCalled()
    const promptArg = client.sessionPrompt.mock.calls[0]?.[0] as { sessionID: string; directory: string; model?: unknown; parts: unknown[]; system?: string }
    expect(promptArg.sessionID).toMatch(/^ses_/)
    expect(promptArg.directory).toBe("/tmp/projA")
    expect(promptArg.model).toEqual({ providerID: "openai", modelID: "gpt-5" })
    expect(promptArg.system).toBe("[mohist variant:high]")
    expect(promptArg.parts).toEqual([{ type: "text", text: "do the work" }])
  })

  it("Returns a null finalAssistantText when the prompt has no text parts", async () => {
    const { deps } = buildRuntime({ promptResult: { data: { info: { id: "msg_1" }, parts: [{ type: "step-start" }] } } })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do the work",
    }, new AbortController().signal)
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value.facts.finalAssistantText).toBeNull()
  })
})

describe("OpenCodeRuntime.runTurn — model/variant non-rotation", () => {
  it("A model change reuses the same physical Session id (no rotation, no extra createSession)", async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const first = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "first",
      options: { model: { providerID: "openai", modelID: "gpt-5" }, variant: "high", unknownKeys: undefined },
    }, new AbortController().signal)
    expect(first.ok).toBe(true)
    if (!first.ok) return
    const firstSessionId = first.value.facts.runtimeSessionId
    client.sessionGet.mockImplementationOnce(async () => ({ data: { id: firstSessionId } }))

    const second = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: firstSessionId, workDir: "/tmp/projA" },
      prompt: "second",
      options: { model: { providerID: "anthropic", modelID: "claude-sonnet-4" }, variant: null, unknownKeys: undefined },
    }, new AbortController().signal)
    expect(second.ok).toBe(true)
    if (!second.ok) return

    expect(second.value.facts.runtimeSessionId).toBe(firstSessionId)
    expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    expect(client.sessionPrompt).toHaveBeenCalledTimes(2)
    const secondPrompt = client.sessionPrompt.mock.calls[1]?.[0] as { model?: unknown; system?: string }
    expect(secondPrompt.model).toEqual({ providerID: "anthropic", modelID: "claude-sonnet-4" })
    expect(secondPrompt.system).toBeUndefined()
  })

  it("Variant change reuses the same physical Session id and updates only the system marker", async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const first = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "first",
      options: { model: null, variant: "high", unknownKeys: undefined },
    }, new AbortController().signal)
    if (!first.ok) throw new Error("first turn failed")
    const sessionId = first.value.facts.runtimeSessionId
    client.sessionGet.mockImplementationOnce(async () => ({ data: { id: sessionId } }))

    const second = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: sessionId, workDir: "/tmp/projA" },
      prompt: "second",
      options: { model: null, variant: "low", unknownKeys: undefined },
    }, new AbortController().signal)
    expect(second.ok).toBe(true)
    if (!second.ok) return
    expect(second.value.facts.runtimeSessionId).toBe(sessionId)
    expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    const secondPrompt = client.sessionPrompt.mock.calls[1]?.[0] as { system?: string }
    expect(secondPrompt.system).toBe("[mohist variant:low]")
  })
})

describe("OpenCodeRuntime.runTurn — input validation", () => {
  it("Multi-slash model is passed through as provider + remaining id without rotation", async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
      options: {
        model: { providerID: "openrouter", modelID: "vendor/family/model" },
        variant: null,
        unknownKeys: undefined,
      },
    }, new AbortController().signal)
    expect(result.ok).toBe(true)
    if (!result.ok) return
    const promptArg = client.sessionPrompt.mock.calls[0]?.[0] as { model?: unknown }
    expect(promptArg.model).toEqual({ providerID: "openrouter", modelID: "vendor/family/model" })
  })

  it("Unknown option keys are surfaced as info diagnostics and do not fail the turn", async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
      options: {
        model: null,
        variant: null,
        unknownKeys: ["type", "livenessQuietThresholdMs"],
      },
    }, new AbortController().signal)
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value.diagnostics.some((d) => d.code === "options-unknown-keys")).toBe(true)
    expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
  })

  it("A non-string variant fails actionably with invalid-input before any SDK call", async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
      options: { model: null, variant: 42 as unknown as string, unknownKeys: undefined },
    }, new AbortController().signal)
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe("invalid-input")
    expect(result.error.message).toMatch(/options\.variant/)
    expect(client.sessionCreate).not.toHaveBeenCalled()
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })
})

describe("OpenCodeRuntime.runTurn — unrestorable binding", () => {
  it("Reusing a Session whose physical id no longer resolves returns missing-session with a Reset hint", async () => {
    const { deps, client } = buildRuntime()
    client.sessionGet.mockImplementationOnce(async () => {
      const err = new Error("not found") as Error & { status?: number }
      err.status = 404
      throw err
    })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: "ses_gone", workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe("missing-session")
    expect(result.error.diagnostics.some((d) => /reset/i.test(d.message))).toBe(true)
    expect(client.sessionCreate).not.toHaveBeenCalled()
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })
})

describe("OpenCodeRuntime.runTurn — single in-flight work prompt", () => {
  it("Two concurrent work prompts on the same binding are rejected for the second", async () => {
    const { deps, client } = buildRuntime()
    client.sessionGet.mockImplementation(async () => ({ data: { id: "ses_same" } }))
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    let resolvePrompt: (value: unknown) => void = () => {}
    const slowPrompt = new Promise((resolve) => {
      resolvePrompt = resolve
    })
    client.sessionPrompt.mockImplementationOnce(async () => {
      await slowPrompt
      return { data: { info: { id: "msg_1" }, parts: [{ type: "text", text: "first" }] } }
    })

    const first = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: "ses_same", workDir: "/tmp/projA" },
      prompt: "first",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))

    const second = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: "ses_same", workDir: "/tmp/projA" },
      prompt: "second",
    }, new AbortController().signal)
    expect(second.ok).toBe(false)
    if (second.ok) return
    expect(second.error.kind).toBe("unavailable-runtime")
    expect(second.error.diagnostics.some((d) => d.code === "in-flight")).toBe(true)

    resolvePrompt({})
    await first
  })
})

describe("OpenCodeRuntime.runTurn — deadline abort on silent hang", () => {
  it("A silently hanging turn is aborted via client.session.abort() and returns interrupted when the executor signal aborts", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client, subscription } = buildRuntime()
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))

      const controller = new AbortController()
      const turnPromise = runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "silent hang",
      }, controller.signal)
      let settled = false
      void turnPromise.then(() => { settled = true })
      await vi.advanceTimersByTimeAsync(10)
      subscription.emit({ type: "session.idle", sessionID: "ses_/tmp/projA" })

      controller.abort()
      const result = await turnPromise

      expect(settled).toBe(true)
      expect(result.ok).toBe(false)
      if (result.ok) return
      expect(result.error.kind).toBe("interrupted")
      expect(client.sessionAbort).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })

  it("An idle event while the prompt is in flight does not complete the turn", async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    let resolvePrompt: (value: unknown) => void = () => {}
    client.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
      resolvePrompt = resolve
    }))

    const turnPromise = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "in flight",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({ type: "session.idle", sessionID: "ses_/tmp/projA" })
    await new Promise((resolve) => setImmediate(resolve))

    let settled = false
    void turnPromise.then(() => { settled = true })
    expect(settled).toBe(false)

    resolvePrompt({ data: { info: { id: "msg_1" }, parts: [{ type: "text", text: "ok" }] } })
    const result = await turnPromise
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value.facts.finalAssistantText).toBe("ok")
  })
})

describe("OpenCodeRuntime.runTurn — provider-error failure policy", () => {
  it("Quota/credit/billing pattern on the first retry event aborts and fails the turn with the provider message as diagnostics", async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))

    const turnPromise = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({
      type: "session.status",
      sessionID: "ses_/tmp/projA",
      payload: {
        sessionID: "ses_/tmp/projA",
        status: { type: "retry", attempt: 1, message: "OpenAI quota exceeded", next: 5000 },
      },
    })

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe("turn-failed")
    expect(result.error.diagnostics.some((d) => /OpenAI quota/.test(d.message))).toBe(true)
    expect(result.error.diagnostics.some((d) => d.code === "provider-quota-exhausted")).toBe(true)
    expect(client.sessionAbort).toHaveBeenCalledTimes(1)
    expect(client.sessionAbort).toHaveBeenCalledWith(
      { sessionID: "ses_/tmp/projA", directory: "/tmp/projA" },
      { throwOnError: true },
    )
  })

  it("The provider wording from the failed workflow triggers first-occurrence failure", async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))
    const turnPromise = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({
      type: "session.status",
      sessionID: "ses_/tmp/projA",
      payload: {
        sessionID: "ses_/tmp/projA",
        status: {
          type: "retry",
          attempt: 1,
          message: "您已达到每周/每月使用上限，您的限额将在 2026-07-19 11:32:48 重置。",
          next: 1000,
        },
      },
    })

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe("turn-failed")
    expect(result.error.diagnostics.some((d) => d.code === "provider-quota-exhausted")).toBe(true)

    client.sessionGet.mockResolvedValueOnce({ data: { id: "ses_/tmp/projA" } })
    const next = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: "ses_/tmp/projA", workDir: "/tmp/projA" },
      prompt: "continue with another model",
      options: { model: { providerID: "openai", modelID: "gpt-5" }, variant: null },
    }, new AbortController().signal)
    expect(next.ok).toBe(true)
    expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    expect(client.sessionPrompt.mock.calls[1]?.[0]).toMatchObject({
      sessionID: "ses_/tmp/projA",
      model: { providerID: "openai", modelID: "gpt-5" },
    })
  })

  it("A quota retry for another Session does not abort the current turn", async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    let resolvePrompt: (value: unknown) => void = () => {}
    client.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => { resolvePrompt = resolve }))
    const turnPromise = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({
      type: "session.status", sessionID: DEFAULT_SESSION_ID, directory: "/tmp/other-project",
      payload: { sessionID: DEFAULT_SESSION_ID, status: { type: "retry", attempt: 1, message: "quota exceeded" } },
    })
    subscription.emit({
      type: "session.status",
      sessionID: "ses_other",
      payload: {
        sessionID: "ses_other",
        status: { type: "retry", attempt: 1, message: "quota exceeded", next: 1000 },
      },
    })
    await new Promise((resolve) => setImmediate(resolve))
    expect(client.sessionAbort).not.toHaveBeenCalled()
    resolvePrompt({ data: { parts: [{ type: "text", text: "done" }] } })
    expect((await turnPromise).ok).toBe(true)
  })

  it("An unconfirmed abort reports abort-unconfirmed instead of a stopped turn", async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))
    client.sessionAbort.mockResolvedValueOnce({ data: false })
    const turnPromise = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))
    subscription.emit({
      type: "session.status",
      sessionID: "ses_/tmp/projA",
      payload: {
        sessionID: "ses_/tmp/projA",
        status: { type: "retry", attempt: 1, message: "quota exceeded", next: 1000 },
      },
    })

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.diagnostics.some((d) => d.code === "abort-unconfirmed")).toBe(true)
    expect(result.error.diagnostics.some((d) => d.code === "provider-quota-exhausted")).toBe(true)
  })

  it("A Session that remains busy after abort reports abort-unconfirmed", async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))
    const turnPromise = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))
    client.sessionStatus.mockResolvedValueOnce({ data: { "ses_/tmp/projA": { type: "busy" } } })
    subscription.emit({
      type: "session.status",
      sessionID: "ses_/tmp/projA",
      payload: { sessionID: "ses_/tmp/projA", status: { type: "retry", attempt: 1, message: "quota exceeded" } },
    })

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.diagnostics.some((d) => d.code === "abort-unconfirmed")).toBe(true)
  })

  it("A reconnected event stream restores a quota verdict from session.status", async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))
    const turnPromise = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))
    client.sessionStatus.mockResolvedValueOnce({
      data: {
        "ses_/tmp/projA": {
          type: "retry",
          attempt: 1,
          message: "Token Plan usage limit reached",
          next: 1000,
        },
      },
    })
    subscription.emit({ type: "server.connected", payload: {} })

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.diagnostics.some((d) => d.code === "provider-quota-exhausted")).toBe(true)
    expect(client.sessionAbort).toHaveBeenCalledTimes(1)
  })

  it("A recoverable transient error that completes within N retries continues", async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    let resolvePrompt: (value: unknown) => void = () => {}
    client.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
      resolvePrompt = resolve
    }))
    const turnPromise = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))

    for (let attempt = 1; attempt <= 4; attempt += 1) {
      subscription.emit({
        type: "session.status",
        sessionID: "ses_/tmp/projA",
        payload: {
          sessionID: "ses_/tmp/projA",
          status: { type: "retry", attempt, message: "rate limit exceeded", next: 200 },
        },
      })
    }

    resolvePrompt({ data: { info: { id: "msg_1" }, parts: [{ type: "text", text: "after 4 retries" }] } })
    const result = await turnPromise
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.value.facts.finalAssistantText).toBe("after 4 retries")
    expect(client.sessionAbort).not.toHaveBeenCalled()
  })

  it("A recoverable error that retries past the consecutive-retry threshold aborts and fails", async () => {
    const { deps, client, subscription } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))

    const turnPromise = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))

    for (let attempt = 1; attempt <= 5; attempt += 1) {
      subscription.emit({
        type: "session.status",
        sessionID: "ses_/tmp/projA",
        payload: {
          sessionID: "ses_/tmp/projA",
          status: { type: "retry", attempt, message: "transient 5xx", next: 1000 },
        },
      })
    }

    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe("turn-failed")
    expect(result.error.diagnostics.some((d) => d.code === "provider-retry-threshold")).toBe(true)
    expect(client.sessionAbort).toHaveBeenCalledTimes(1)
  })

  it("Custom policy: configurable threshold and patterns are honoured", async () => {
    const { deps, client, subscription } = buildRuntime({
      policy: { nonRecoverablePatterns: [/^payment-required$/], consecutiveRetryThreshold: 2 },
    })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    let resolvePrompt: (value: unknown) => void = () => {}
    client.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
      resolvePrompt = resolve
    }))
    const turnPromise = runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({
      type: "session.status",
      sessionID: "ses_/tmp/projA",
      payload: {
        sessionID: "ses_/tmp/projA",
        status: { type: "retry", attempt: 1, message: "OpenAI quota exceeded", next: 1000 },
      },
    })
    await new Promise((resolve) => setImmediate(resolve))

    subscription.emit({
      type: "session.status",
      sessionID: "ses_/tmp/projA",
      payload: {
        sessionID: "ses_/tmp/projA",
        status: { type: "retry", attempt: 2, message: "still retrying", next: 1000 },
      },
    })
    const result = await turnPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe("turn-failed")
    expect(result.error.diagnostics.some((d) => d.code === "provider-retry-threshold")).toBe(true)
    expect(client.sessionAbort).toHaveBeenCalledTimes(1)
    resolvePrompt({})
  })
})

describe("OpenCodeRuntime.runTurn — restart reconciliation", () => {
  it("Reconciles state from session.status/get/messages on reconnect without V2 replay state", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client, subscription } = buildRuntime({ rebuildDelayMs: 50 })
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()

      const first = await runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "first",
      }, new AbortController().signal)
      if (!first.ok) throw new Error("first turn failed")
      const sessionId = first.value.facts.runtimeSessionId
      client.sessionGet.mockImplementation(async () => ({ data: { id: sessionId } }))

      subscription.emit({ type: "server.disconnected", payload: {} })
      expect(runtime.ready()).toBe(false)
      await vi.advanceTimersByTimeAsync(60)
      expect(runtime.ready()).toBe(true)

      client.sessionStatus.mockClear()
      client.sessionGet.mockClear()
      client.sessionMessages.mockClear()
      client.sessionGet.mockImplementation(async () => ({ data: { id: sessionId } }))

      const reconnect = await runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: sessionId, workDir: "/tmp/projA" },
        prompt: "second after reconnect",
      }, new AbortController().signal)
      expect(reconnect.ok).toBe(true)
      if (!reconnect.ok) return
      expect(reconnect.value.facts.runtimeSessionId).toBe(sessionId)
      expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe("OpenCodeRuntime.runTurn — no auto-replay on uncertain prompt admission", () => {
  it("Does not auto-resubmit when the awaited prompt rejects with an unknown error", async () => {
    const { deps, client } = buildRuntime()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    client.sessionPrompt.mockImplementationOnce(async () => {
      throw new Error("connection reset before result")
    })
    const result = await runtime.runTurn({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
      prompt: "do",
    }, new AbortController().signal)
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe("turn-failed")
    expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
  })
})
