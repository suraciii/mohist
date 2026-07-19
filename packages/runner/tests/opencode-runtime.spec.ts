import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  errorKindFor,
  isNonRecoverableProviderMessage,
  isNonRecoverableProviderRetry,
  normalizeInterrupted,
  normalizeInvalidInput,
  normalizeMissingSession,
  normalizePermissionRequired,
  normalizeTurnFailed,
  normalizeUnavailableRuntime,
  OpenCodeRuntime,
  parseModelIdentifier,
  setOpenCodeRuntimeFactoryForTest,
  getOpenCodeRuntimeFactory,
  createDefaultOpenCodeRuntime,
} from "../src/runtime/opencode/index.js"
import type { OpenCodeRuntimeDeps } from "../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../src/runtime/opencode/server-process.js"
import type { CatalogClient } from "../src/runtime/opencode/catalog.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import type { RuntimeModelCatalog } from "../src/runtime/opencode/types.js"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import * as runtimeModule from "../src/runtime/opencode/index.js"

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

interface FakeClientHandles {
  health: ReturnType<typeof vi.fn>
  providerList: ReturnType<typeof vi.fn>
  modelList: ReturnType<typeof vi.fn>
  sessionCreate: ReturnType<typeof vi.fn>
  globalEvent: ReturnType<typeof vi.fn>
}

interface BuildArgs {
  failStart?: boolean
  failHealth?: boolean
  failCatalog?: boolean
  failSessionCreate?: boolean
  catalog?: RuntimeModelCatalog
  rebuildDelayMs?: number
}

interface BuildResult {
  deps: OpenCodeRuntimeDeps
  subscription: FakeSubscription
  client: FakeClientHandles
  closed: { value: boolean }
}

function buildDeps(args: BuildArgs = {}): BuildResult {
  const subscription = new FakeSubscription()
  const closed = { value: false }
  const catalog: RuntimeModelCatalog = args.catalog ?? {
    models: [
      { providerID: "openai", modelID: "gpt-5", variants: ["low", "high"] },
      { providerID: "anthropic", modelID: "claude-sonnet-4", variants: [] },
    ],
    fetchedAt: 0,
  }
  const health = vi.fn(async () => ({ data: { ok: true } }))
  const providerList = vi.fn(async () => ({ data: { data: [] } }))
  const modelList = vi.fn(async () => ({
    data: {
      data: catalog.models.map((m) => ({
        id: m.modelID,
        providerID: m.providerID,
        variants: m.variants.map((id) => ({ id })),
      })),
    },
  }))
  const sessionCreate = vi.fn(async (params: { directory?: string; model?: unknown }) => ({
    data: { id: `ses_${(params.directory ?? "default").replace(/[^a-z0-9]+/gi, "_")}` },
  }))
  const globalEvent = vi.fn(async () => ({
    stream: (async function* () {
      // No events emitted by default; fakes drive the subscription.
    })(),
  }))
  if (args.failHealth) {
    health.mockRejectedValueOnce(new Error("health boom"))
  }
  if (args.failCatalog) {
    modelList.mockRejectedValueOnce(new Error("catalog boom"))
  }
  if (args.failSessionCreate) {
    sessionCreate.mockRejectedValueOnce(new Error("session create boom"))
  }
  const clientProxy = {
    global: { health, event: globalEvent },
    v2: { provider: { list: providerList }, model: { list: modelList } },
    session: { create: sessionCreate },
  }
  const clientHandles: FakeClientHandles = {
    health,
    providerList,
    modelList,
    sessionCreate,
    globalEvent,
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
    serverFactory: async () => {
      if (args.failStart) throw new Error("spawn failed")
      return server
    },
    catalogFactory: () => {
      const c: CatalogClient = {
        async list() {
          if (args.failCatalog) throw new Error("catalog boom")
          return catalog
        },
      }
      return c
    },
    eventSubscriptionFactory: () => subscription,
    ...(args.rebuildDelayMs !== undefined ? { rebuildDelayMs: args.rebuildDelayMs } : {}),
  }
  return { deps, subscription, client: clientHandles, closed }
}

describe("parseModelIdentifier", () => {
  it("parses a simple provider/model", () => {
    expect(parseModelIdentifier("openai/gpt-5")).toEqual({
      kind: "ok",
      value: { providerID: "openai", modelID: "gpt-5" },
    })
  })

  it("preserves the full remainder for multi-slash model IDs", () => {
    expect(parseModelIdentifier("openrouter/vendor/family/model")).toEqual({
      kind: "ok",
      value: { providerID: "openrouter", modelID: "vendor/family/model" },
    })
  })

  it("rejects an empty model", () => {
    expect(parseModelIdentifier("").kind).toBe("failure")
  })

  it("rejects an identifier without a slash", () => {
    expect(parseModelIdentifier("gpt-5").kind).toBe("failure")
  })

  it("rejects an identifier with an empty provider", () => {
    expect(parseModelIdentifier("/gpt-5").kind).toBe("failure")
  })

  it("rejects an identifier with an empty model id", () => {
    expect(parseModelIdentifier("openai/").kind).toBe("failure")
  })
})

describe("error normalization", () => {
  it("errorKindFor maps 404 to missing-session", () => {
    expect(errorKindFor({ message: "not found", status: 404 })).toBe("missing-session")
  })

  it("errorKindFor maps permission messages to permission-required", () => {
    expect(errorKindFor({ message: "permission denied", status: 403 })).toBe("permission-required")
  })

  it("errorKindFor falls back to turn-failed for unknown errors", () => {
    expect(errorKindFor({ message: "boom" })).toBe("turn-failed")
  })

  it("isNonRecoverableProviderMessage matches quota wording", () => {
    expect(isNonRecoverableProviderMessage("OpenAI quota exceeded")).toBe(true)
  })

  it("isNonRecoverableProviderMessage matches credit wording", () => {
    expect(isNonRecoverableProviderMessage("No credits remaining on your account")).toBe(true)
  })

  it("isNonRecoverableProviderMessage matches billing wording", () => {
    expect(isNonRecoverableProviderMessage("Billing issue: please update your card")).toBe(true)
  })

  it("isNonRecoverableProviderMessage matches Chinese wording", () => {
    expect(isNonRecoverableProviderMessage("账户额度已用完")).toBe(true)
  })

  it("isNonRecoverableProviderMessage matches usage-limit wording without matching rate limits", () => {
    expect(isNonRecoverableProviderMessage("Token Plan usage limit reached")).toBe(true)
    expect(isNonRecoverableProviderMessage("您已达到每周/每月使用上限，您的限额将在明天重置")).toBe(true)
    expect(isNonRecoverableProviderMessage("Rate limit exceeded, retry shortly")).toBe(false)
  })

  it("isNonRecoverableProviderRetry prefers structured quota reasons", () => {
    expect(isNonRecoverableProviderRetry({
      message: "retry later",
      action: { reason: "free_tier_limit" },
    })).toBe(true)
  })

  it("isNonRecoverableProviderMessage does not match a transient 429", () => {
    expect(isNonRecoverableProviderMessage("Rate limit exceeded, retry shortly")).toBe(false)
  })

  it("normalizeMissingSession includes a Reset hint", () => {
    const error = normalizeMissingSession()
    expect(error.kind).toBe("missing-session")
    expect(error.diagnostics.some((d) => d.message.toLowerCase().includes("reset"))).toBe(true)
  })

  it("normalizePermissionRequired is the only outcome of an unsatisfiable permission request — no auto-approve", () => {
    const error = normalizePermissionRequired()
    expect(error.kind).toBe("permission-required")
    expect(error.diagnostics.some((d) => /approve|grant|out-of-band/i.test(d.message))).toBe(true)
  })

  it("normalizeUnavailableRuntime carries a recovery diagnostic", () => {
    const error = normalizeUnavailableRuntime()
    expect(error.kind).toBe("unavailable-runtime")
    expect(error.diagnostics.length).toBeGreaterThan(0)
  })

  it("normalizeTurnFailed carries the provider message as a diagnostic, not as the error message", () => {
    const error = normalizeTurnFailed({ message: "OpenAI quota exceeded" })
    expect(error.kind).toBe("turn-failed")
    expect(error.message).toBe("OpenCode turn failed")
    expect(error.diagnostics.some((d) => d.message.includes("OpenAI quota"))).toBe(true)
  })

  it("normalizeTurnFailed exposes a stable local transport failure without exposing the raw payload in the message", () => {
    const error = normalizeTurnFailed({
      message: "fetch failed",
      cause: { code: "UND_ERR_HEADERS_TIMEOUT", message: "Headers Timeout Error" },
    })
    expect(error.message).toContain("UND_ERR_HEADERS_TIMEOUT")
    expect(error.diagnostics.some((d) => d.code === "opencode-transport-failed")).toBe(true)
  })

  it("normalizeInvalidInput echoes the message", () => {
    const error = normalizeInvalidInput("model must be a string")
    expect(error.kind).toBe("invalid-input")
    expect(error.message).toBe("model must be a string")
  })

  it("normalizeInterrupted is informational", () => {
    const error = normalizeInterrupted()
    expect(error.kind).toBe("interrupted")
    expect(error.diagnostics.some((d) => d.severity === "info")).toBe(true)
  })
})

describe("OpenCodeRuntime boundary", () => {
  it("only exports Mohist-owned types and helpers (no SDK DTOs)", () => {
    const surface = runtimeModule as Record<string, unknown>
    const forbiddenPrefixes = ["OpencodeClient", "OpencodeServer", "V2Model", "V2Provider", "ProviderV2", "ModelV2", "Session2", "HeyApi"]
    for (const name of Object.keys(surface)) {
      for (const prefix of forbiddenPrefixes) {
        expect(name.startsWith(prefix)).toBe(false)
      }
    }
  })

  it("does not re-export createOpencodeServer, createOpencodeClient, or OpencodeClient", () => {
    const surface = runtimeModule as Record<string, unknown>
    expect(surface["createOpencodeServer"]).toBeUndefined()
    expect(surface["createOpencodeClient"]).toBeUndefined()
    expect(surface["OpencodeClient"]).toBeUndefined()
    expect(surface["OpencodeServer"]).toBeUndefined()
  })
})

describe("OpenCodeRuntime readiness contract", () => {
  it("is not ready before start()", () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    expect(runtime.ready()).toBe(false)
    expect(runtime.diagnostic()).toBeNull()
    expect(runtime.catalog()).toBeNull()
  })

  it("ready() becomes true only after health AND catalog both pass", async () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    const start = await runtime.start()
    expect(start.ok).toBe(true)
    expect(runtime.ready()).toBe(true)
    expect(runtime.diagnostic()).toBeNull()
    expect(runtime.catalog()?.models.length).toBe(2)
  })

  it("stays not ready when the health check fails and surfaces a diagnostic", async () => {
    const { deps } = buildDeps({ failHealth: true })
    const runtime = new OpenCodeRuntime(deps)
    const result = await runtime.start()
    if (result.ok) throw new Error("expected start to fail")
    expect(runtime.ready()).toBe(false)
    expect(result.error.kind).toBe("unavailable-runtime")
    expect(runtime.diagnostic()?.code).toBe("health-failed")
  })

  it("stays not ready when the catalog load fails and surfaces a diagnostic", async () => {
    const { deps } = buildDeps({ failCatalog: true })
    const runtime = new OpenCodeRuntime(deps)
    const result = await runtime.start()
    if (result.ok) throw new Error("expected start to fail")
    expect(runtime.ready()).toBe(false)
    expect(runtime.diagnostic()?.code).toBe("catalog-load-failed")
  })

  it("emits an actionable diagnostic when the server cannot start", async () => {
    const { deps } = buildDeps({ failStart: true })
    const runtime = new OpenCodeRuntime(deps)
    const result = await runtime.start()
    if (result.ok) throw new Error("expected start to fail")
    expect(runtime.ready()).toBe(false)
    expect(result.error.kind).toBe("unavailable-runtime")
    expect(runtime.diagnostic()?.code).toBe("server-spawn-failed")
  })

  it("start() is idempotent while ready", async () => {
    const { deps, closed } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    const first = await runtime.start()
    expect(first.ok).toBe(true)
    const second = await runtime.start()
    expect(second.ok).toBe(true)
    expect(closed.value).toBe(false)
  })
})

describe("OpenCodeRuntime on simulated server exit", () => {
  it("ready() becomes false, in-flight createSession fails, and a background rebuild re-passes readiness", async () => {
    vi.useFakeTimers()
    try {
      const { deps, subscription } = buildDeps({ rebuildDelayMs: 50 })
      const runtime = new OpenCodeRuntime(deps)
      const started = await runtime.start()
      expect(started.ok).toBe(true)
      expect(runtime.ready()).toBe(true)

      const before = await runtime.createSession({ target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" } })
      if (!before.ok) throw new Error("expected createSession to succeed before exit")
      expect(before.value.runtimeSessionId).toBe("ses__tmp_projA")

      subscription.emit({ type: "server.disconnected", payload: {} })

      expect(runtime.ready()).toBe(false)
      expect(runtime.diagnostic()?.code).toBe("server-exit")

      const during = await runtime.createSession({ target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" } })
      if (during.ok) throw new Error("expected createSession to fail during rebuild")
      expect(during.error.kind).toBe("unavailable-runtime")

      await vi.advanceTimersByTimeAsync(50)
      expect(runtime.ready()).toBe(true)
      expect(runtime.diagnostic()).toBeNull()

      const after = await runtime.createSession({ target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projB" } })
      if (!after.ok) throw new Error(`expected createSession to succeed after rebuild: ${after.error.kind}`)
      expect(after.value.workDir).toBe("/tmp/projB")
    } finally {
      vi.useRealTimers()
    }
  })
})

describe("OpenCodeRuntime.createSession", () => {
  it("returns a Mohist-owned runtime session id", async () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.createSession({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
    })
    if (!result.ok) throw new Error(`expected createSession to succeed: ${result.error.kind}`)
    expect(result.value.runtimeSessionId).toBe("ses__tmp_projA")
    expect(result.value.workDir).toBe("/tmp/projA")
  })

  it("fails with unavailable-runtime when the runtime is not ready", async () => {
    const { deps } = buildDeps()
    const runtime = new OpenCodeRuntime(deps)
    const result = await runtime.createSession({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
    })
    if (result.ok) throw new Error("expected createSession to fail")
    expect(result.error.kind).toBe("unavailable-runtime")
  })

  it("normalizes a session-create error as turn-failed with diagnostics", async () => {
    const { deps } = buildDeps({ failSessionCreate: true })
    const runtime = new OpenCodeRuntime(deps)
    await runtime.start()
    const result = await runtime.createSession({
      target: { runtime: "opencode", runtimeSessionId: null, workDir: "/tmp/projA" },
    })
    if (result.ok) throw new Error("expected createSession to fail")
    expect(result.error.kind).toBe("turn-failed")
  })
})

describe("factory seam", () => {
  beforeEach(() => setOpenCodeRuntimeFactoryForTest(null))
  afterEach(() => setOpenCodeRuntimeFactoryForTest(null))

  it("setOpenCodeRuntimeFactoryForTest(null) restores the default factory", () => {
    const defaultFactory = createDefaultOpenCodeRuntime
    setOpenCodeRuntimeFactoryForTest(() => {
      throw new Error("should not be called")
    })
    setOpenCodeRuntimeFactoryForTest(null)
    expect(getOpenCodeRuntimeFactory()).toBe(defaultFactory)
  })

  it("getOpenCodeRuntimeFactory returns a function that builds an OpenCodeRuntime", () => {
    const factory = getOpenCodeRuntimeFactory()
    const built = factory({
      directory: "/tmp/work",
      serverFactory: async () => {
        throw new Error("not used")
      },
      catalogFactory: () => ({ async list() { return { models: [], fetchedAt: 0 } } }),
      eventSubscriptionFactory: () => ({
        subscribe() { return () => {} },
        async close() {},
      }),
    })
    expect(built).toBeInstanceOf(OpenCodeRuntime)
  })

  it("setOpenCodeRuntimeFactoryForTest replaces the factory", () => {
    const replacement = () => {
      throw new Error("custom factory")
    }
    setOpenCodeRuntimeFactoryForTest(replacement)
    expect(getOpenCodeRuntimeFactory()).toBe(replacement)
  })
})
