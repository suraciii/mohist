import { afterEach, describe, expect, it, vi } from "vitest"
import {
  OpenCodeRuntime,
  type RuntimeProviderErrorPolicy,
} from "../src/runtime/opencode/index.js"
import {
  DEADLINE_WARNING_TEXT,
  WARNING_WINDOW_MS,
} from "../src/runtime/opencode/turn.js"
import type { OpenCodeRuntimeDeps } from "../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"

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
  sessionCreate: ReturnType<typeof vi.fn>
  sessionPrompt: ReturnType<typeof vi.fn>
  sessionPromptAsync: ReturnType<typeof vi.fn>
  sessionAbort: ReturnType<typeof vi.fn>
  sessionGet: ReturnType<typeof vi.fn>
}

interface BuildArgs {
  failPrompt?: boolean
  failPromptAsync?: boolean
  promptResult?: unknown
  createId?: (params: { directory?: string }) => string
  policy?: RuntimeProviderErrorPolicy
  abortResult?: boolean
}

interface BuildResult {
  deps: OpenCodeRuntimeDeps
  client: FakeClientHandles
  server: OpencodeServerHandle
}

function buildRuntime(args: BuildArgs = {}): BuildResult {
  const subscription = new FakeSubscription()
  const closed = { value: false }
  const sessionCreate = vi.fn(async (params: { directory?: string; model?: unknown }) => {
    const id = args.createId ? args.createId(params) : `ses_${(params.directory ?? "default").replace(/[^a-z0-9]+/gi, "_")}`
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
    return { data: true }
  })

  const sessionAbort = vi.fn(async (_params: { sessionID: string; directory?: string }) => ({ data: args.abortResult ?? true }))
  const sessionGet = vi.fn(async () => ({ data: { id: "ses_1" } }))
  const sessionStatus = vi.fn(async () => ({ data: {} }))

  const clientProxy = {
    global: { health: vi.fn(async () => ({ data: { ok: true } })), event: vi.fn() },
    session: {
      create: sessionCreate,
      prompt: sessionPrompt,
      promptAsync: sessionPromptAsync,
      abort: sessionAbort,
      get: sessionGet,
      messages: vi.fn(),
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
    sessionCreate,
    sessionPrompt,
    sessionPromptAsync,
    sessionAbort,
    sessionGet,
  }
  const deps: OpenCodeRuntimeDeps = {
    directory: "/tmp/work",
    serverFactory: async () => server,
    eventSubscriptionFactory: () => subscription,
    ...(args.policy ? { providerErrorPolicy: args.policy } : {}),
  }
  return { deps, client, server }
}

afterEach(() => {
  vi.useRealTimers()
})

describe("DEADLINE_WARNING_TEXT — task-agnostic invariant", () => {
  it("Mentions a static human-readable wrap-up signal", () => {
    expect(DEADLINE_WARNING_TEXT).toMatch(/5 minutes/i)
    expect(DEADLINE_WARNING_TEXT).toMatch(/commit/i)
    expect(DEADLINE_WARNING_TEXT).toMatch(/progress/i)
    expect(DEADLINE_WARNING_TEXT).toMatch(/end the turn/i)
  })

  it("Names no marker, file, or task/profile identifier", () => {
    expect(DEADLINE_WARNING_TEXT).not.toMatch(/unfinished/i)
    expect(DEADLINE_WARNING_TEXT).not.toMatch(/promise/i)
    expect(DEADLINE_WARNING_TEXT).not.toMatch(/progress\.txt/i)
    expect(DEADLINE_WARNING_TEXT).not.toMatch(/\.md\b/i)
    expect(DEADLINE_WARNING_TEXT).not.toMatch(/T-\d{3}/i)
    expect(DEADLINE_WARNING_TEXT).not.toMatch(/wr_[0-9a-f]+/i)
    expect(DEADLINE_WARNING_TEXT).not.toMatch(/opencode/i)
  })

  it("Exposes the warning window as a 5-minute constant", () => {
    expect(WARNING_WINDOW_MS).toBe(5 * 60 * 1000)
  })
})

describe("OpenCodeRuntime.runTurn — deadline declaration opt-out", () => {
  it("A request with no deadlineMs runs the prompt and never calls session.promptAsync", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime()
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      const result = await runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "do",
      }, new AbortController().signal)
      expect(result.ok).toBe(true)
      await vi.advanceTimersByTimeAsync(60 * 60 * 1000)
      expect(client.sessionPromptAsync).not.toHaveBeenCalled()
      expect(client.sessionAbort).not.toHaveBeenCalled()
    } finally {
      vi.useRealTimers()
    }
  })

  it("An invalid deadlineMs (zero, negative, non-finite) is treated as omitted", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime()
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      const result = await runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "do",
        deadlineMs: 0,
      }, new AbortController().signal)
      expect(result.ok).toBe(true)
      await vi.advanceTimersByTimeAsync(60 * 60 * 1000)
      expect(client.sessionPromptAsync).not.toHaveBeenCalled()
    } finally {
      vi.useRealTimers()
    }
  })
})

describe("OpenCodeRuntime.runTurn — warning injection (deadline > warning window)", () => {
  it("Injects the wrap-up warning exactly once at deadline - WARNING_WINDOW_MS", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime()
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      let resolvePrompt: (value: unknown) => void = () => {}
      client.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
        resolvePrompt = resolve
      }))

      const deadlineMs = 30 * 60 * 1000
      const turnPromise = runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "long running",
        deadlineMs,
      }, new AbortController().signal)
      await vi.advanceTimersByTimeAsync(1)

      expect(client.sessionPromptAsync).not.toHaveBeenCalled()

      await vi.advanceTimersByTimeAsync(deadlineMs - WARNING_WINDOW_MS - 2)
      expect(client.sessionPromptAsync).not.toHaveBeenCalled()

      await vi.advanceTimersByTimeAsync(1)
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)
      const warningArg = client.sessionPromptAsync.mock.calls[0]?.[0] as {
        sessionID: string
        directory: string
        parts: Array<{ type: string; text: string }>
      }
      expect(warningArg.sessionID).toMatch(/^ses_/)
      expect(warningArg.directory).toBe("/tmp/projA")
      expect(warningArg.parts).toEqual([{ type: "text", text: DEADLINE_WARNING_TEXT }])

      await vi.advanceTimersByTimeAsync(WARNING_WINDOW_MS - 1)
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)

      resolvePrompt({ data: { info: { id: "msg_1" }, parts: [{ type: "text", text: "done" }] } })
      const result = await turnPromise
      expect(result.ok).toBe(true)
      if (!result.ok) return
      expect(result.value.facts.finalAssistantText).toBe("done")
      expect(client.sessionAbort).not.toHaveBeenCalled()
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe("OpenCodeRuntime.runTurn — warning injection (deadline <= warning window)", () => {
  it("Injects the warning at turn start when deadline <= WARNING_WINDOW_MS, and not a second time", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime()
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      let resolvePrompt: (value: unknown) => void = () => {}
      client.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
        resolvePrompt = resolve
      }))

      const deadlineMs = 4 * 60 * 1000
      const turnPromise = runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "short deadline",
        deadlineMs,
      }, new AbortController().signal)
      await vi.advanceTimersByTimeAsync(0)
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)
      const warningArg = client.sessionPromptAsync.mock.calls[0]?.[0] as {
        parts: Array<{ type: string; text: string }>
      }
      expect(warningArg.parts).toEqual([{ type: "text", text: DEADLINE_WARNING_TEXT }])

      await vi.advanceTimersByTimeAsync(deadlineMs - 1)
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)

      resolvePrompt({ data: { info: { id: "msg_1" }, parts: [{ type: "text", text: "completed" }] } })
      const result = await turnPromise
      expect(result.ok).toBe(true)
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })

  it("Warns once at turn start when deadline equals WARNING_WINDOW_MS exactly", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime()
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      let resolvePrompt: (value: unknown) => void = () => {}
      client.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
        resolvePrompt = resolve
      }))

      const turnPromise = runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "exact window",
        deadlineMs: WARNING_WINDOW_MS,
      }, new AbortController().signal)
      await vi.advanceTimersByTimeAsync(1)
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)
      await vi.advanceTimersByTimeAsync(WARNING_WINDOW_MS)
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)
      resolvePrompt({ data: { info: { id: "msg_1" }, parts: [{ type: "text", text: "done" }] } })
      await turnPromise
    } finally {
      vi.useRealTimers()
    }
  })
})

describe("OpenCodeRuntime.runTurn — warned turn that ends normally is not aborted", () => {
  it("Does not call session.abort when the prompt completes after the warning was injected", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime()
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      let resolvePrompt: (value: unknown) => void = () => {}
      client.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
        resolvePrompt = resolve
      }))

      const deadlineMs = 30 * 60 * 1000
      const turnPromise = runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "agent wraps up",
        deadlineMs,
      }, new AbortController().signal)
      await vi.advanceTimersByTimeAsync(deadlineMs - WARNING_WINDOW_MS)
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)

      resolvePrompt({ data: { info: { id: "msg_1" }, parts: [{ type: "text", text: "wrapped" }] } })
      const result = await turnPromise
      expect(result.ok).toBe(true)
      if (!result.ok) return
      expect(result.value.facts.finalAssistantText).toBe("wrapped")
      expect(client.sessionAbort).not.toHaveBeenCalled()
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })

  it("Does not call session.abort when the prompt completes before the warning is due", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime()
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      let resolvePrompt: (value: unknown) => void = () => {}
      client.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
        resolvePrompt = resolve
      }))

      const deadlineMs = 30 * 60 * 1000
      const turnPromise = runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "fast finish",
        deadlineMs,
      }, new AbortController().signal)
      await vi.advanceTimersByTimeAsync(60 * 1000)

      expect(client.sessionPromptAsync).not.toHaveBeenCalled()
      resolvePrompt({ data: { info: { id: "msg_1" }, parts: [{ type: "text", text: "fast" }] } })
      const result = await turnPromise
      expect(result.ok).toBe(true)
      expect(client.sessionAbort).not.toHaveBeenCalled()

      await vi.advanceTimersByTimeAsync(deadlineMs)
      expect(client.sessionPromptAsync).not.toHaveBeenCalled()
    } finally {
      vi.useRealTimers()
    }
  })
})

describe("OpenCodeRuntime.runTurn — deadline abort", () => {
  it("Calls session.abort and returns deadline-exceeded when the runner deadline fires before the prompt resolves", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime()
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))

      const deadlineMs = 30 * 60 * 1000
      const turnPromise = runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "hangs past deadline",
        deadlineMs,
      }, new AbortController().signal)
      await vi.advanceTimersByTimeAsync(deadlineMs - WARNING_WINDOW_MS)
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)

      await vi.advanceTimersByTimeAsync(WARNING_WINDOW_MS)
      const result = await turnPromise
      expect(result.ok).toBe(false)
      if (result.ok) return
      expect(result.error.kind).toBe("deadline-exceeded")
      expect(result.error.message).toBe(`OpenCode turn timed out after ${deadlineMs / 1000}s`)
      expect(result.error.diagnostics.some((diagnostic) => diagnostic.code === "deadline-exceeded")).toBe(true)
      expect(client.sessionAbort).toHaveBeenCalledTimes(1)
      expect(client.sessionAbort.mock.calls[0]?.[0]).toEqual({
        sessionID: expect.stringMatching(/^ses_/),
        directory: "/tmp/projA",
      })
      expect(client.sessionPromptAsync).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })

  it("Keeps deadline-exceeded when abort cannot be confirmed and does not create a replacement Session", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime({ abortResult: false })
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      client.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))

      const deadlineMs = 60_000
      const turnPromise = runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "hangs past deadline",
        deadlineMs,
      }, new AbortController().signal)
      await vi.advanceTimersByTimeAsync(deadlineMs)

      const result = await turnPromise
      expect(result.ok).toBe(false)
      if (result.ok) return
      expect(result.error.kind).toBe("deadline-exceeded")
      expect(result.error.diagnostics.some((diagnostic) => diagnostic.code === "abort-unconfirmed")).toBe(true)
      expect(client.sessionCreate).toHaveBeenCalledTimes(1)
      expect(client.sessionAbort).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe("OpenCodeRuntime.runTurn — warning injection failure is non-fatal", () => {
  it("Swallows a rejected promptAsync and emits exactly one info-level diagnostic, leaving the turn to complete", async () => {
    vi.useFakeTimers()
    try {
      const { deps, client } = buildRuntime({ failPromptAsync: true })
      const runtime = new OpenCodeRuntime(deps)
      await runtime.start()
      let resolvePrompt: (value: unknown) => void = () => {}
      client.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
        resolvePrompt = resolve
      }))

      const deadlineMs = 30 * 60 * 1000
      const turnPromise = runtime.runTurn({
        target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
        prompt: "warn failure",
        deadlineMs,
      }, new AbortController().signal)
      await vi.advanceTimersByTimeAsync(deadlineMs - WARNING_WINDOW_MS)

      const warnings = client.sessionPromptAsync.mock.calls
      expect(warnings.length).toBeGreaterThanOrEqual(1)
      resolvePrompt({ data: { info: { id: "msg_1" }, parts: [{ type: "text", text: "still ran" }] } })
      const result = await turnPromise
      expect(result.ok).toBe(true)
      if (!result.ok) return
      const diagnostic = result.value.diagnostics.find((d) => d.code === "deadline-warning-injection-failed")
      expect(diagnostic).toBeDefined()
      expect(diagnostic?.severity).toBe("info")
      const sameCodeCount = result.value.diagnostics.filter((d) => d.code === "deadline-warning-injection-failed").length
      expect(sameCodeCount).toBe(1)
      expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })
})
