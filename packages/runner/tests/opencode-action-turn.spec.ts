import { afterEach, describe, expect, it, vi } from "vitest"
import {
  opencodeAction,
  parseOpencodeInput,
  buildTurnRequest,
  DEFAULT_TURN_DEADLINE_MS,
} from "../src/actions/opencode.js"
import { OpenCodeRuntime } from "../src/runtime/opencode/index.js"
import type { OpenCodeRuntimeDeps } from "../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import type { RuntimeModelCatalog, RuntimeProviderErrorPolicy } from "../src/runtime/opencode/types.js"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type { ActionContext } from "../src/core/types.js"
import { clearOpenCodeRuntimeFactoryForTest } from "./support/opencode-runtime-factory.js"
import { setPromptLoaderRegistryForTest } from "../src/core/prompt.js"

class FakeSubscription implements RuntimeEventSubscription {
  private listeners = new Set<(event: RuntimeGlobalEvent) => void>()
  closed = false
  subscribe(listener: (event: RuntimeGlobalEvent) => void): () => void {
    if (this.closed) return () => {}
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }
  emit(event: RuntimeGlobalEvent): void {
    for (const listener of [...this.listeners]) listener(event)
  }
  async close(): Promise<void> {
    this.closed = true
    this.listeners.clear()
  }
}

interface FakeClient {
  sessionCreate: ReturnType<typeof vi.fn>
  sessionPrompt: ReturnType<typeof vi.fn>
  sessionPromptAsync: ReturnType<typeof vi.fn>
  sessionAbort: ReturnType<typeof vi.fn>
  sessionGet: ReturnType<typeof vi.fn>
}

interface BuildArgs {
  promptResult?: unknown
  promptImplementation?: () => Promise<unknown>
  failCreate?: boolean
  policy?: RuntimeProviderErrorPolicy
  sessionIdForGet?: string
}

interface BuildResult {
  deps: OpenCodeRuntimeDeps
  runtime: OpenCodeRuntime
  client: FakeClient
  subscription: FakeSubscription
}

function buildRuntime(args: BuildArgs = {}): BuildResult {
  const subscription = new FakeSubscription()
  const closed = { value: false }
  const catalog: RuntimeModelCatalog = {
    models: [{ providerID: "openai", modelID: "gpt-5", variants: ["low", "high"] }],
    fetchedAt: 0,
  }
  const sessionCreate = vi.fn(async (_params: { directory?: string; model?: unknown }) => {
    if (args.failCreate) throw new Error("create boom")
    return { data: { id: `ses_${(closed.value ? "new" : "default").replace(/[^a-z0-9]+/gi, "_")}` } }
  })
  const sessionPrompt = vi.fn(async (_params: { sessionID: string; directory?: string; parts?: unknown }) => {
    if (args.promptImplementation) return await args.promptImplementation()
    if (args.promptResult !== undefined) return args.promptResult
    return {
      data: {
        info: { id: "msg_1", sessionID: "ses_default", role: "assistant" },
        parts: [{ type: "text", text: "hello from opencode" }],
      },
    }
  })
  const sessionAbort = vi.fn(async (_params: { sessionID: string; directory?: string }) => ({ data: true }))
  const sessionPromptAsync = vi.fn(async (_params: { sessionID: string; directory?: string; parts?: unknown }) => ({ data: true }))
  const sessionGet = vi.fn(async (_params: { sessionID: string; directory?: string }) => {
    if (args.sessionIdForGet) return { data: { id: args.sessionIdForGet } }
    return { data: { id: _params.sessionID } }
  })
  const clientProxy = {
    global: { health: vi.fn(async () => ({ data: { ok: true } })), event: vi.fn() },
    v2: { provider: { list: vi.fn(async () => ({ data: { data: [] } })) }, model: { list: vi.fn(async () => ({ data: { data: catalog.models.map((m) => ({ id: m.modelID, providerID: m.providerID, variants: m.variants.map((id) => ({ id })) })) } })) } },
    session: { create: sessionCreate, prompt: sessionPrompt, promptAsync: sessionPromptAsync, abort: sessionAbort, get: sessionGet, messages: vi.fn(), status: vi.fn(async () => ({ data: {} })) },
  }
  const server: OpencodeServerHandle = {
    url: "http://fake",
    directory: "/tmp/work",
    client: clientProxy as unknown as OpencodeClient,
    async close() { closed.value = true },
  }
  const deps: OpenCodeRuntimeDeps = {
    directory: "/tmp/work",
    serverFactory: async () => server,
    catalogFactory: () => ({ async list() { return catalog } }),
    eventSubscriptionFactory: () => subscription,
    ...(args.policy ? { providerErrorPolicy: args.policy } : {}),
  }
  const runtime = new OpenCodeRuntime(deps)
  void runtime.start()
  return {
    deps,
    runtime,
    client: { sessionCreate, sessionPrompt, sessionPromptAsync, sessionAbort, sessionGet },
    subscription,
  }
}

async function ensureReady(runtime: OpenCodeRuntime): Promise<void> {
  await runtime.start()
}

function baseContext(overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "work-1",
    workType: "task",
    stage: "build",
    title: "Opencode turn",
    uses: "mohist/opencode",
    with: { prompt: "do the work" } as never,
    variables: {},
    workDir: "/tmp/work",
    signal: new AbortController().signal,
    projectId: "proj-1",
    writeVars: async () => {},
    ...overrides,
  }
}

afterEach(() => {
  setPromptLoaderRegistryForTest(null)
  clearOpenCodeRuntimeFactoryForTest()
  vi.useRealTimers()
})

describe("parseOpencodeInput — input validation", () => {
  it("Rejects non-string model", () => {
    const result = parseOpencodeInput({ options: { model: 42 as never } })
    expect(result.kind).toBe("failure")
    if (result.kind !== "failure") return
    expect(result.result.error?.message).toMatch(/options\.model.*must be a string/)
  })

  it("Rejects non-string variant", () => {
    const result = parseOpencodeInput({ options: { variant: true as never } })
    expect(result.kind).toBe("failure")
    if (result.kind !== "failure") return
    expect(result.result.error?.message).toMatch(/options\.variant.*must be a string/)
  })

  it("Rejects malformed model (no slash)", () => {
    const result = parseOpencodeInput({ options: { model: "no-slash" } })
    expect(result.kind).toBe("failure")
  })

  it("Accepts multi-slash model verbatim", () => {
    const result = parseOpencodeInput({ options: { model: "openrouter/vendor/family/model" } })
    expect(result.kind).toBe("ok")
    if (result.kind !== "ok") return
    expect(result.options?.model).toBe("openrouter/vendor/family/model")
  })

  it("Ignores legacy option keys (type, liveness) without failing", () => {
    const result = parseOpencodeInput({
      options: { model: "openai/gpt-5", variant: "high", type: "opencode", livenessQuietThresholdMs: 5000 } as never,
    })
    expect(result.kind).toBe("ok")
    if (result.kind !== "ok") return
    expect(result.options?.model).toBe("openai/gpt-5")
    expect(result.options?.variant).toBe("high")
  })
})

describe("opencodeAction — happy path + turn fact", () => {
  it("Runs via OpenCodeRuntime.runTurn (not runAcpWorkflowAgentSession) and populates finalAssistantText", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const context = baseContext({ openCodeRuntime: runtime })
    const result = await opencodeAction(context)
    expect(result.error).toBeUndefined()
    expect(result.turnFact).toEqual({ finalAssistantText: "hello from opencode" })
  })

  it("prepends JSON-safe read-only parent background on every applicable turn", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const parentIssueContext = {
      title: "Parent </parent> \"title\"",
      body: "## Requirement\n```json\n{ \"delimiter\": \"${{ child.scope }}\" }\n```\n---END---",
    }
    const originalPrompt = "  original child task prompt\n<artifact id=\"child\">keep exactly</artifact>  "
    const context = baseContext({
      openCodeRuntime: runtime,
      stage: "plan",
      parentIssueContext,
      with: { prompt: originalPrompt } as never,
    })

    await opencodeAction(context)
    await opencodeAction({ ...context, workId: "work-2" })

    const expected = `Parent issue context (read-only background; JSON):\n${JSON.stringify(parentIssueContext)}\n\nTreat the parent issue context above as read-only background. The current child issue body is authoritative and controls delivery scope.\n\n${originalPrompt}`
    const submitted = client.sessionPrompt.mock.calls.map((call) => {
      const request = call[0] as { parts: Array<{ type: string; text: string }> }
      return request.parts.map((part) => part.text).join("")
    })
    expect(submitted).toEqual([expected, expected])
    expect(submitted[0]).toContain(JSON.stringify(parentIssueContext))
    expect(submitted[0]).toMatch(/read-only background/i)
    expect(submitted[0]).toMatch(/current child issue body.*authoritative.*controls delivery scope/i)
    expect(submitted[0]?.endsWith(originalPrompt)).toBe(true)
  })

  it("submits the resolved prompt byte-for-byte when parent context is absent", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const originalPrompt = "  exact prompt\nwith markdown --- and trailing spaces  "

    await opencodeAction(baseContext({
      openCodeRuntime: runtime,
      stage: "plan",
      with: { prompt: originalPrompt } as never,
    }))

    const request = client.sessionPrompt.mock.calls[0]?.[0] as { parts: Array<{ type: string; text: string }> }
    expect(request.parts.map((part) => part.text).join("")).toBe(originalPrompt)
  })

  it("Action does not synthesize { promise } in its output (executor projects it from markers)", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const context = baseContext({
      openCodeRuntime: runtime,
      with: { prompt: "<promise>PASS</promise>" } as never,
    })
    const result = await opencodeAction(context)
    const output = result.output as Record<string, unknown>
    expect(output.promise).toBeUndefined()
    expect(output.kind).toBe("opencode")
  })

  it("Persists a new physical Session before the first prompt and reuses it across tasks", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    let runtimeSessionId: string | null = null
    const openWorkflowAgentSession = vi.fn(async () => ({ runtimeSessionId, workDir: "/tmp/work" }))
    const attachWorkflowAgentSession = vi.fn(async (_projectId: string, _workflowRunId: string, _sessionName: string, body: unknown) => {
      runtimeSessionId = (body as { runtimeSessionId: string }).runtimeSessionId
    })
    const serverConnection = {
      openWorkflowAgentSession,
      attachWorkflowAgentSession,
    } as unknown as NonNullable<ActionContext["serverConnection"]>
    const first = await opencodeAction(baseContext({
      openCodeRuntime: runtime,
      serverConnection,
      with: { session: "plan", prompt: "first task" } as never,
    }))
    const second = await opencodeAction(baseContext({
      openCodeRuntime: runtime,
      serverConnection,
      workId: "work-2",
      with: { session: "plan", prompt: "second task" } as never,
    }))

    expect(first.error).toBeUndefined()
    expect(second.error).toBeUndefined()
    expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    expect(client.sessionPrompt).toHaveBeenCalledTimes(2)
    expect(attachWorkflowAgentSession).toHaveBeenCalledTimes(1)
    expect(attachWorkflowAgentSession.mock.invocationCallOrder[0])
      .toBeLessThan(client.sessionPrompt.mock.invocationCallOrder[0] ?? Number.MAX_SAFE_INTEGER)
    expect(client.sessionPrompt.mock.calls.map((call) => (call[0] as { sessionID: string }).sessionID))
      .toEqual(["ses_default", "ses_default"])
  })

  it("Does not submit the first prompt when persisting the new binding fails", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const serverConnection = {
      openWorkflowAgentSession: vi.fn(async () => ({ runtimeSessionId: null, workDir: "/tmp/work" })),
      attachWorkflowAgentSession: vi.fn(async () => { throw new Error("attach rejected") }),
    } as unknown as NonNullable<ActionContext["serverConnection"]>

    const result = await opencodeAction(baseContext({
      openCodeRuntime: runtime,
      serverConnection,
      with: { session: "plan", prompt: "do not submit" } as never,
    }))

    expect(result.error).toBeDefined()
    expect(result.error?.message).toMatch(/persist.*binding.*attach rejected/i)
    expect(client.sessionCreate).toHaveBeenCalledTimes(1)
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })

  it("Rejects a persisted Session from a different workspace before creating or prompting", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const serverConnection = {
      openWorkflowAgentSession: vi.fn(async () => ({ runtimeSessionId: "ses_old", workDir: "/tmp/old-workspace" })),
      attachWorkflowAgentSession: vi.fn(),
    } as unknown as NonNullable<ActionContext["serverConnection"]>

    const result = await opencodeAction(baseContext({
      openCodeRuntime: runtime,
      serverConnection,
      with: { session: "plan", prompt: "do not use old workspace" } as never,
    }))

    expect(result.error).toBeDefined()
    expect(result.error?.message).toMatch(/different workspace/i)
    expect(client.sessionCreate).not.toHaveBeenCalled()
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })

  it("Aborts the physical Session and exposes a stable error when prompt transport times out", async () => {
    const transportCause = Object.assign(new Error("Headers Timeout Error"), {
      code: "UND_ERR_HEADERS_TIMEOUT",
    })
    const { runtime, client } = buildRuntime({
      promptImplementation: async () => {
        throw new TypeError("fetch failed", { cause: transportCause })
      },
    })
    await ensureReady(runtime)

    const result = await opencodeAction(baseContext({ openCodeRuntime: runtime }))

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("UND_ERR_HEADERS_TIMEOUT")
    expect(client.sessionAbort).toHaveBeenCalledTimes(1)
  })

  it("Action passes multi-slash model as provider + remainder without rotation", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const context = baseContext({
      openCodeRuntime: runtime,
      with: { prompt: "do", options: { model: "openrouter/vendor/family/model", variant: "high" } } as never,
    })
    const result = await opencodeAction(context)
    expect(result.error).toBeUndefined()
    const arg = client.sessionPrompt.mock.calls[0]?.[0] as { model?: unknown }
    expect(arg.model).toEqual({ providerID: "openrouter", modelID: "vendor/family/model" })
  })

  it("Rejects unknown option keys with a diagnostic in the runtime, not a failure", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const context = baseContext({
      openCodeRuntime: runtime,
      with: { prompt: "do", options: { model: "openai/gpt-5", type: "opencode", livenessQuietThresholdMs: 5000 } } as never,
    })
    const result = await opencodeAction(context)
    expect(result.error).toBeUndefined()
    expect(client.sessionPrompt).toHaveBeenCalledTimes(1)
  })
})

describe("opencodeAction — readiness gates", () => {
  it("Fails when the OpenCode runtime handle is missing", async () => {
    const context = baseContext()
    const result = await opencodeAction(context)
    expect(result.error).toBeDefined()
    expect(result.error?.message).toMatch(/requires the opencode runtime/i)
  })

  it("Fails when the OpenCode runtime is not ready", async () => {
    const { runtime } = buildRuntime()
    await runtime.start()
    await runtime.shutdown({ clearDiagnostic: true })
    const context = baseContext({ openCodeRuntime: runtime })
    const result = await opencodeAction(context)
    expect(result.error).toBeDefined()
    expect(result.error?.message).toMatch(/requires the opencode runtime to be ready/i)
  })
})

describe("opencodeAction — input validation short-circuits before runtime", () => {
  it("Rejects blank prompt", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const context = baseContext({
      openCodeRuntime: runtime,
      with: { prompt: "   " } as never,
    })
    const result = await opencodeAction(context)
    expect(result.error).toBeDefined()
    expect(result.error?.message).toMatch(/requires 'prompt'/)
  })

  it("Rejects malformed options without calling sessionPrompt", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const context = baseContext({
      openCodeRuntime: runtime,
      with: { prompt: "do", options: { model: "no-slash" } } as never,
    })
    const result = await opencodeAction(context)
    expect(result.error).toBeDefined()
    expect(result.error?.message).toMatch(/provider\/model/)
    expect(client.sessionPrompt).not.toHaveBeenCalled()
  })
})

describe("opencodeAction — provider-error fail-fast propagates", () => {
  it("Provider quota on the first retry event aborts and surfaces the failure", async () => {
    const { runtime, subscription, client } = buildRuntime({
      promptImplementation: () => new Promise(() => {}),
    })
    await ensureReady(runtime)
    const context = baseContext({ openCodeRuntime: runtime })
    const actionPromise = opencodeAction(context)
    await new Promise((resolve) => setImmediate(resolve))
    subscription.emit({
      type: "session.status",
      sessionID: "ses_default",
      payload: {
        sessionID: "ses_default",
        status: { type: "retry", attempt: 1, message: "OpenAI quota exceeded", next: 1000 },
      },
    })
    const result = await actionPromise
    expect(result.error).toBeDefined()
    expect(result.turnFact?.finalAssistantText).toBeNull()
    expect(client.sessionAbort).toHaveBeenCalledTimes(1)
  })
})

describe("buildTurnRequest — deadline declaration", () => {
  it("Places the supplied deadlineMs on the turn request", () => {
    const request = buildTurnRequest(
      { runtimeSessionId: null, workDir: "/tmp/work" },
      "do the work",
      undefined,
      90_000,
    )
    expect(request.deadlineMs).toBe(90_000)
  })

  it("Preserves the prompt and options unchanged when no deadline override is provided", () => {
    const request = buildTurnRequest(
      { runtimeSessionId: null, workDir: "/tmp/work" },
      "do the work",
      undefined,
      DEFAULT_TURN_DEADLINE_MS,
    )
    expect(request.prompt).toBe("do the work")
    expect(request.deadlineMs).toBe(DEFAULT_TURN_DEADLINE_MS)
    expect(request.options).toBeDefined()
  })
})

describe("opencodeAction — deadline declaration", () => {
  it("Defaults the turn deadline to 60 minutes when no override is supplied", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const runTurnSpy = vi.spyOn(runtime, "runTurn")
    const context = baseContext({ openCodeRuntime: runtime })
    await opencodeAction(context)
    expect(runTurnSpy).toHaveBeenCalledTimes(1)
    const request = runTurnSpy.mock.calls[0]?.[0] as { deadlineMs?: number }
    expect(request.deadlineMs).toBe(DEFAULT_TURN_DEADLINE_MS)
  })

  it("Honours with.timeout override in milliseconds", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const runTurnSpy = vi.spyOn(runtime, "runTurn")
    const context = baseContext({
      openCodeRuntime: runtime,
      with: { prompt: "do", timeout: 5 * 60 * 1000 } as never,
    })
    await opencodeAction(context)
    const request = runTurnSpy.mock.calls[0]?.[0] as { deadlineMs?: number }
    expect(request.deadlineMs).toBe(5 * 60 * 1000)
  })

  it("Falls back to the 60-minute default for invalid timeout values", async () => {
    const { runtime } = buildRuntime()
    await ensureReady(runtime)
    const runTurnSpy = vi.spyOn(runtime, "runTurn")
    const context = baseContext({
      openCodeRuntime: runtime,
      with: { prompt: "do", timeout: 0 } as never,
    })
    await opencodeAction(context)
    const request = runTurnSpy.mock.calls[0]?.[0] as { deadlineMs?: number }
    expect(request.deadlineMs).toBe(DEFAULT_TURN_DEADLINE_MS)
  })

  it("Does not surface the deadline value in the prompt body sent to OpenCode", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const context = baseContext({
      openCodeRuntime: runtime,
      with: { prompt: "do", timeout: 5 * 60 * 1000 } as never,
    })
    const result = await opencodeAction(context)
    expect(result.error).toBeUndefined()
    const promptArg = client.sessionPrompt.mock.calls[0]?.[0] as {
      parts: Array<{ type: string; text: string }>
      system?: string
    }
    const flat = JSON.stringify(promptArg)
    expect(flat).not.toMatch(/300000/)
    expect(flat).not.toMatch(/60 minutes? remaining/i)
    expect(flat).not.toMatch(/5 minutes? remaining/i)
    expect(flat).not.toMatch(/deadline/i)
    expect(promptArg.system).toBeUndefined()
  })

  it("Does not surface the deadline value in the prompt body when using the default", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const context = baseContext({ openCodeRuntime: runtime })
    const result = await opencodeAction(context)
    expect(result.error).toBeUndefined()
    const promptArg = client.sessionPrompt.mock.calls[0]?.[0] as {
      parts: Array<{ type: string; text: string }>
      system?: string
    }
    const flat = JSON.stringify(promptArg)
    expect(flat).not.toMatch(/3600000/)
    expect(flat).not.toMatch(/60 minutes? remaining/i)
    expect(flat).not.toMatch(/deadline/i)
    expect(promptArg.system).toBeUndefined()
  })

  it("The warning text injected by the runtime never appears in the initial prompt body", async () => {
    const { runtime, client } = buildRuntime()
    await ensureReady(runtime)
    const context = baseContext({
      openCodeRuntime: runtime,
      with: { prompt: "do", timeout: 30 * 60 * 1000 } as never,
    })
    const result = await opencodeAction(context)
    expect(result.error).toBeUndefined()
    const promptArg = client.sessionPrompt.mock.calls[0]?.[0] as {
      parts: Array<{ type: string; text: string }>
    }
    const initialText = promptArg.parts.map((p) => p.text).join(" ")
    expect(initialText).not.toMatch(/interrupted/i)
    expect(initialText).not.toMatch(/commit/i)
    expect(initialText).not.toMatch(/progress/i)
  })
})
