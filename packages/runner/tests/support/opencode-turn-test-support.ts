import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import { vi } from "vitest"
import type { RuntimeProviderErrorPolicy } from "../../src/runtime/opencode/index.js"
import type { OpenCodeRuntimeDeps } from "../../src/runtime/opencode/runtime.js"
import type { OpencodeServerHandle } from "../../src/runtime/opencode/server-process.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../../src/runtime/opencode/event-subscription.js"

export const DEFAULT_SESSION_ID = "ses_/tmp/projA"

export class FakeSubscription implements RuntimeEventSubscription {
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

export interface FakeClientHandles {
  health: ReturnType<typeof vi.fn>
  sessionCreate: ReturnType<typeof vi.fn>
  sessionPrompt: ReturnType<typeof vi.fn>
  sessionPromptAsync: ReturnType<typeof vi.fn>
  sessionAbort: ReturnType<typeof vi.fn>
  sessionMessages: ReturnType<typeof vi.fn>
  sessionGet: ReturnType<typeof vi.fn>
  sessionStatus: ReturnType<typeof vi.fn>
  instanceDispose: ReturnType<typeof vi.fn>
}

export interface BuildRuntimeArgs {
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

export interface BuildRuntimeResult {
  deps: OpenCodeRuntimeDeps
  subscription: FakeSubscription
  client: FakeClientHandles
  server: OpencodeServerHandle
}

export function buildRuntime(args: BuildRuntimeArgs = {}): BuildRuntimeResult {
  const subscription = new FakeSubscription()
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
  const instanceDispose = vi.fn(async () => ({ data: true }))
  const clientProxy = {
    global: { health, event: vi.fn(async () => ({ stream: (async function* () { void subscription })() })) },
    session: { create: sessionCreate, prompt: sessionPrompt, promptAsync: sessionPromptAsync, abort: sessionAbort, messages: sessionMessages, get: sessionGet, status: sessionStatus },
    instance: { dispose: instanceDispose },
  }
  const server: OpencodeServerHandle = {
    url: "http://fake",
    directory: "/tmp/work",
    client: clientProxy as unknown as OpencodeClient,
    async close() {},
  }
  const client: FakeClientHandles = { health, sessionCreate, sessionPrompt, sessionPromptAsync, sessionAbort, sessionMessages, sessionGet, sessionStatus, instanceDispose }
  const deps: OpenCodeRuntimeDeps = {
    directory: "/tmp/work",
    serverFactory: async () => server,
    eventSubscriptionFactory: () => subscription,
    ...(args.policy ? { providerErrorPolicy: args.policy } : {}),
    ...(args.rebuildDelayMs !== undefined ? { rebuildDelayMs: args.rebuildDelayMs } : {}),
  }
  return { deps, subscription, client, server }
}
