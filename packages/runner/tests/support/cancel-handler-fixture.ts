import { RunnerSignalRClient, type CancelAgentSessionPayload } from "../../src/server/runner-signalr.js"
import type { AgentSessionRuntimeEventOutbox } from "../../src/server/runtime-event-outbox.js"
import type { CancelOperationJournalEntry, CancelOperationJournalStore } from "../../src/runtime/cancel-operation-journal.js"
import type {
  OpenCodeRuntime,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeResult,
} from "../../src/runtime/opencode/index.js"
import { currentSignalRTestState } from "./signalr-test-resources.js"

type AnyFn = (...args: any[]) => any

export interface CancelCapturedBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
}

export interface FakeRuntimeHandles {
  runtime: OpenCodeRuntime
  cancelCalls: RuntimeCancelRequest[]
  setCancelResult: (result: RuntimeResult<RuntimeCancelResult>) => void
  setResolveResult: (result: RuntimeResult<{ runtimeSessionId: string; workDir: string; activeTurn: boolean }>) => void
  setReady: (ready: boolean) => void
}

export function makeFakeRuntime(): FakeRuntimeHandles {
  const cancelCalls: RuntimeCancelRequest[] = []
  let ready = true
  let nextResult: RuntimeResult<RuntimeCancelResult> = {
    ok: true,
    value: {
      facts: { runtimeSessionId: "ses_runtime", workDir: "/work/project", cancelled: true, stopConfirmed: true },
      diagnostics: [],
    },
    diagnostics: [],
  }
  let nextResolveResult: RuntimeResult<{ runtimeSessionId: string; workDir: string; activeTurn: boolean }> = {
    ok: true,
    value: { runtimeSessionId: "runtime-1", workDir: "/work/project", activeTurn: true },
    diagnostics: [],
  }
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => ready,
    diagnostic: () => null,
    async cancel(request: RuntimeCancelRequest): Promise<RuntimeResult<RuntimeCancelResult>> {
      cancelCalls.push(request)
      return nextResult
    },
    async resolveSession() {
      return nextResolveResult
    },
  }
  return {
    runtime: runtime as OpenCodeRuntime,
    cancelCalls,
    setCancelResult(result) { nextResult = result },
    setResolveResult(result) { nextResolveResult = result },
    setReady(value) { ready = value },
  }
}

export function buildClient(opts: {
  resolver?: AnyFn | null
  outbox?: AgentSessionRuntimeEventOutbox | null
  openCodeRuntime?: OpenCodeRuntime | (() => OpenCodeRuntime | null) | null
  piRuntime?: unknown
  cancelOperationJournal?: CancelOperationJournalStore | null
}): RunnerSignalRClient {
  currentSignalRTestState().builders.length = 0
  const resolver = opts.resolver === undefined ? null : opts.resolver
  const openCodeRuntime = opts.openCodeRuntime === undefined ? makeFakeRuntime().runtime : opts.openCodeRuntime
  return new RunnerSignalRClient(
    "https://runner.test",
    "runner-1",
    "/virtual/projects",
    null,
    {
      followupTargetResolver: resolver as never,
      agentSessionRuntimeEventOutbox: opts.outbox ?? null,
      openCodeRuntime: openCodeRuntime as never,
      ...(opts.piRuntime !== undefined ? { piRuntime: opts.piRuntime as never } : {}),
      ...(opts.cancelOperationJournal !== undefined ? { cancelOperationJournal: opts.cancelOperationJournal } : {}),
    },
  )
}

export function lastBuilder(): CancelCapturedBuilder {
  const builder = currentSignalRTestState().builders.at(-1) as CancelCapturedBuilder | undefined
  if (!builder) throw new Error("no captured builder; construct a RunnerSignalRClient first")
  return builder
}

export class MemoryCancelOperationJournal implements CancelOperationJournalStore {
  private readonly entries = new Map<string, CancelOperationJournalEntry>()

  async load(): Promise<void> {}
  async get(sessionId: string, operationId: string): Promise<CancelOperationJournalEntry | null> {
    return this.entries.get(`${sessionId}:${operationId}`) ?? null
  }
  async start(sessionId: string, payload: CancelAgentSessionPayload): Promise<CancelOperationJournalEntry> {
    const key = `${sessionId}:${payload.operationId}`
    const existing = this.entries.get(key)
    if (existing) return existing
    const entry: CancelOperationJournalEntry = { request: structuredClone(payload), state: "started" }
    this.entries.set(key, entry)
    return entry
  }
  async complete(sessionId: string, payload: CancelAgentSessionPayload, reply: { state: string; interruptUnconfirmed?: boolean }): Promise<void> {
    this.entries.set(`${sessionId}:${payload.operationId}`, { request: structuredClone(payload), state: "completed", reply })
  }
}

export function readyOutbox(): AgentSessionRuntimeEventOutbox {
  return {
    ready: () => true,
    load: async () => {},
    recover: async () => {},
    enqueueBeforeExecution: async () => {},
    enqueueProducedFact: async () => {},
    enqueueProducedFactBatch: async () => {},
    kick: async () => {},
    stop: async () => {},
    snapshot: () => [],
  }
}

export function emitCancel(builder: CancelCapturedBuilder, payload: CancelAgentSessionPayload | null | undefined): Promise<unknown> {
  const handler = builder.handlers.get("CancelAgentSession")
  if (!handler) throw new Error("CancelAgentSession handler was not registered")
  return Promise.resolve(handler(payload))
}

export function genericCancelPayload(sessionId: string): CancelAgentSessionPayload {
  return {
    target: {
      kind: "generic",
      projectId: "proj-1",
      sessionId,
      binding: { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" },
    },
  }
}
