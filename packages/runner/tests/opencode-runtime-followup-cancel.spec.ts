import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  OpenCodeRuntime,
  type RuntimeCancelRequest,
  RuntimeError,
  type RuntimeFollowupRequest,
  type RuntimeResult,
} from "../src/runtime/opencode/index.js"
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
    for (const listener of [...this.listeners]) {
      listener(event)
    }
  }
  async close(): Promise<void> {
    this.closed = true
    this.listeners.clear()
  }
}

interface BuildArgs {
  failSessionGet?: boolean
  missingSessionOnGet?: boolean
  failPrompt?: boolean
  failAbort?: boolean
  missingSessionOnPrompt?: boolean
  missingSessionOnAbort?: boolean
}

interface BuildResult {
  deps: OpenCodeRuntimeDeps
  subscription: FakeSubscription
  client: {
    sessionGet: ReturnType<typeof vi.fn>
    sessionStatus: ReturnType<typeof vi.fn>
    sessionPrompt: ReturnType<typeof vi.fn>
    sessionAbort: ReturnType<typeof vi.fn>
  }
}

function buildDeps(args: BuildArgs = {}): BuildResult {
  const subscription = new FakeSubscription()
  const closed = { value: false }
  const sessionGet = vi.fn(async (params: { sessionID: string }) => {
    if (args.failSessionGet) throw new Error("session.get boom")
    if (args.missingSessionOnGet) {
      const error = new Error("not found") as Error & { status?: number }
      error.status = 404
      throw error
    }
    return { data: { id: params.sessionID } }
  })
  const sessionStatus = vi.fn(async () => ({ data: {} }))
  const sessionPrompt = vi.fn(async (params: { sessionID: string }) => {
    if (args.failPrompt) throw new Error("prompt boom")
    if (args.missingSessionOnPrompt) {
      const error = new Error("not found") as Error & { status?: number }
      error.status = 404
      throw error
    }
    return {
      data: {
        info: { id: "msg_followup", sessionID: params.sessionID, role: "assistant" },
        parts: [{ id: "part_followup", messageID: "msg_followup", sessionID: params.sessionID, type: "text", text: "followup output" }],
      },
    }
  })
  const sessionAbort = vi.fn(async (params: { sessionID: string }) => {
    if (args.failAbort) throw new Error("abort boom")
    if (args.missingSessionOnAbort) {
      const error = new Error("not found") as Error & { status?: number }
      error.status = 404
      throw error
    }
    return { data: true }
  })
  const clientProxy = {
    global: { health: vi.fn(async () => ({ data: { ok: true } })) },
    session: {
      create: vi.fn(async (params: { directory?: string }) => ({
        data: { id: `ses_${(params.directory ?? "default").replace(/[^a-z0-9]+/gi, "_")}` },
      })),
      get: sessionGet,
      status: sessionStatus,
      prompt: sessionPrompt,
      abort: sessionAbort,
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
  const deps: OpenCodeRuntimeDeps = {
    directory: "/tmp/work",
    serverFactory: async () => server,
    eventSubscriptionFactory: () => subscription,
  }
  return {
    deps,
    subscription,
    client: {
      sessionGet,
      sessionStatus,
      sessionPrompt,
      sessionAbort,
    },
  }
}

describe("OpenCodeRuntime.resolveSession", () => {
  it.each([
    ["busy", true],
    ["idle", false],
  ] as const)("uses typed SDK requests and reports %s active-turn state", async (statusType, activeTurn) => {
    const { deps, client } = buildDeps()
    client.sessionStatus.mockResolvedValueOnce({ data: { ses_existing: { type: statusType } } })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.resolveSession({
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
    })

    expect(result).toMatchObject({ ok: true, value: { activeTurn } })
    expect(client.sessionGet).toHaveBeenCalledWith(
      { sessionID: "ses_existing", directory: "/tmp/work" },
      { throwOnError: true },
    )
    expect(client.sessionStatus).toHaveBeenCalledWith(
      { directory: "/tmp/work" },
      { throwOnError: true },
    )
  })
})

describe("OpenCodeRuntime.followup", () => {
  it("runs client.session.prompt to completion and returns the resolved runtime session facts", async () => {
    const { deps, client } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const request: RuntimeFollowupRequest = {
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
      prompt: "continue the work",
    }
    const result = await runtime.followup(request)
    expect(result.ok).toBe(true)
    if (!result.ok) throw new Error("expected ok")
    expect(result.value.facts.runtimeSessionId).toBe("ses_existing")
    expect(result.value.facts.workDir).toBe("/tmp/work")

    expect(client.sessionGet).toHaveBeenCalledWith(
      { sessionID: "ses_existing", directory: "/tmp/work" },
      { throwOnError: true },
    )
    expect(client.sessionPrompt).toHaveBeenCalledWith({
      sessionID: "ses_existing",
      directory: "/tmp/work",
      parts: [{ type: "text", text: "continue the work" }],
    }, { throwOnError: true })
  })

  it("projects assistant output to the observer before resolving", async () => {
    const { deps, client, subscription } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const observed: string[] = []
    client.sessionPrompt.mockImplementationOnce(async (params: { sessionID: string }) => {
      subscription.emit({
        type: "message.updated",
        sessionID: params.sessionID,
        directory: "/tmp/work",
        payload: {
          info: { id: "msg_observed", sessionID: params.sessionID, role: "assistant" },
          parts: [{ id: "part_observed", messageID: "msg_observed", sessionID: params.sessionID, type: "text", text: "observed output" }],
        },
      })
      return {
        data: {
          info: { id: "msg_observed", sessionID: params.sessionID, role: "assistant" },
          parts: [{ id: "part_observed", messageID: "msg_observed", sessionID: params.sessionID, type: "text", text: "observed output" }],
        },
      }
    })

    const result = await runtime.followup({
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
      prompt: "continue the work",
    }, { onEvent: (event) => observed.push(`${event.type}:${String(event.payload.text ?? "")}`) })

    expect(result).toMatchObject({ ok: true, value: { facts: { finalAssistantText: "observed output" } } })
    expect(observed).toContain("message.delta:observed output")
    expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
  })

  it("applies model and variant on the prompt body without rotating the physical session", async () => {
    const { deps, client } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const request: RuntimeFollowupRequest = {
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
      prompt: "continue the work",
      options: { model: { providerID: "openai", modelID: "gpt-5" }, variant: "high" },
    }
    await runtime.followup(request)

    expect(client.sessionPrompt).toHaveBeenCalledWith({
      sessionID: "ses_existing",
      directory: "/tmp/work",
      parts: [{ type: "text", text: "continue the work" }],
      model: { providerID: "openai", modelID: "gpt-5" },
      variant: "high",
    }, { throwOnError: true })
  })

  it("fails with unavailable-runtime when the runtime is not ready", async () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)

    const result = await runtime.followup({
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
      prompt: "continue the work",
    })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("unavailable-runtime")
  })

  it("fails with invalid-input when the binding is missing (no runtimeSessionId)", async () => {
    const { deps, client } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.followup({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/work" },
      prompt: "continue the work",
    })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("invalid-input")
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })

  it("fails with missing-session when client.session.get returns 404", async () => {
    const { deps } = buildDeps({ missingSessionOnGet: true })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.followup({
      target: { runtime: "opencode", runtimeSessionId: "ses_orphan", workDir: "/tmp/work" },
      prompt: "continue the work",
    })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("missing-session")
    expect(result.error.message.toLowerCase()).toContain("reset")
  })

  it("fails with missing-session when client.session.prompt returns 404", async () => {
    const { deps } = buildDeps({ missingSessionOnPrompt: true })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.followup({
      target: { runtime: "opencode", runtimeSessionId: "ses_orphan", workDir: "/tmp/work" },
      prompt: "continue the work",
    })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("missing-session")
  })

  it("fails with turn-failed when client.session.get throws a non-404 error", async () => {
    const { deps } = buildDeps({ failSessionGet: true })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.followup({
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
      prompt: "continue the work",
    })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("turn-failed")
  })

  it("fails with turn-failed when client.session.prompt throws", async () => {
    const { deps } = buildDeps({ failPrompt: true })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.followup({
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
      prompt: "continue the work",
    })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("turn-failed")
  })

  it("fails with invalid-input when the prompt is empty", async () => {
    const { deps, client } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.followup({
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
      prompt: "   ",
    })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("invalid-input")
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })
})

describe("OpenCodeRuntime.cancel", () => {
  it("calls client.session.abort and returns a cancelled fact", async () => {
    const { deps, client } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const request: RuntimeCancelRequest = {
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
    }
    client.sessionStatus
      .mockResolvedValueOnce({ data: { ses_existing: { type: "busy" } } })
      .mockResolvedValueOnce({ data: { ses_existing: { type: "idle" } } })
    const result = await runtime.cancel(request)
    expect(result.ok).toBe(true)
    if (!result.ok) throw new Error("expected ok")
    expect(result.value.facts.cancelled).toBe(true)
    expect(result.value.facts.stopConfirmed).toBe(true)
    expect(result.value.facts.runtimeSessionId).toBe("ses_existing")
    expect(result.value.facts.workDir).toBe("/tmp/work")
    expect(client.sessionAbort).toHaveBeenCalledWith({
      sessionID: "ses_existing",
      directory: "/tmp/work",
    }, { throwOnError: true })
    expect(client.sessionStatus).toHaveBeenCalledWith({ directory: "/tmp/work" }, { throwOnError: true })
  })

  it("returns a cancelled fact without confirmation when the Session remains busy", async () => {
    const { deps, client } = buildDeps()
    client.sessionStatus
      .mockResolvedValueOnce({ data: { ses_existing: { type: "busy" } } })
      .mockResolvedValueOnce({ data: { ses_existing: { type: "busy" } } })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.cancel({
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
    })

    expect(result).toMatchObject({ ok: true, value: { facts: { cancelled: true, stopConfirmed: false } } })
  })

  it("does not confirm an idle OpenCode session as a stopped turn", async () => {
    const { deps, client } = buildDeps()
    client.sessionStatus.mockResolvedValueOnce({ data: { ses_existing: { type: "idle" } } })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.cancel({
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
    })

    expect(result).toMatchObject({ ok: true, value: { facts: { cancelled: true, stopConfirmed: false } } })
    expect(client.sessionAbort).not.toHaveBeenCalled()
  })

  it("fails with unavailable-runtime when the runtime is not ready", async () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)

    const result = await runtime.cancel({
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
    })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("unavailable-runtime")
  })

  it("fails with invalid-input when the binding is missing (no runtimeSessionId)", async () => {
    const { deps, client } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.cancel({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/work" },
    })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("invalid-input")
    expect(client.sessionAbort).not.toHaveBeenCalled()
  })

  it("fails with missing-session when client.session.abort returns 404", async () => {
    const { deps, client } = buildDeps({ missingSessionOnAbort: true })
    client.sessionStatus.mockResolvedValueOnce({ data: { ses_orphan: { type: "busy" } } })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.cancel({
      target: { runtime: "opencode", runtimeSessionId: "ses_orphan", workDir: "/tmp/work" },
    })
    expect(result.ok).toBe(false)
    if (result.ok) throw new Error("expected failure")
    expect(result.error.kind).toBe("missing-session")
    expect(result.error.message.toLowerCase()).toContain("reset")
  })

  it("returns an unconfirmed cancel fact when client.session.abort throws a non-404 error", async () => {
    const { deps, client } = buildDeps({ failAbort: true })
    client.sessionStatus.mockResolvedValueOnce({ data: { ses_existing: { type: "busy" } } })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()

    const result = await runtime.cancel({
      target: { runtime: "opencode", runtimeSessionId: "ses_existing", workDir: "/tmp/work" },
    })
    expect(result).toMatchObject({ ok: true, value: { facts: { cancelled: true, stopConfirmed: false } } })
  })
})
