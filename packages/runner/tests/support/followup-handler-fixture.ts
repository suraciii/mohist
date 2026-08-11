/**
 * Shared test fixture for the runner-side Follow-up / Cancel SignalR
 * handler tests. Issue-461 T-001 routes both input and operation-
 * correlated terminal outcomes through a host-owned
 * `AgentSessionRuntimeEventOutbox`. The fixture therefore provides:
 *   - a recording in-memory outbox implementation that lets tests
 *     inspect enqueued records without booting a Node filesystem
 *     adapter
 *   - a SignalR client builder that wires the outbox (no longer
 *     the `serverConnection` / `FollowupFailureOutbox` pair)
 *   - a fake OpenCodeRuntime with `followupCalls` / `cancelCalls`
 *     observation surfaces
 */
import { it as vitestIt, vi } from "vitest"
import { HubConnectionState, type HubConnection } from "@microsoft/signalr"
import { RunnerSignalRClient, type ReceiveFollowupPayload } from "../../src/server/runner-signalr.js"
import type { RunnerFileSystem, RunnerResourceContext } from "../../src/system/filesystem.js"
import type {
  AgentSessionRuntimeEventOutbox,
  RuntimeEventOutboxFileSystem,
  RuntimeEventRecord,
  RuntimeEventDelivery,
} from "../../src/server/runtime-event-outbox.js"
import type { AgentSessionRuntimeEventReceipt } from "../../src/server/connection.js"
import type { FollowupOperationJournalStore } from "../../src/runtime/followup-operation-journal.js"
import type { CancelOperationJournalStore } from "../../src/runtime/cancel-operation-journal.js"
import { MemoryFileSystem } from "./memory-filesystem.js"
import { currentSignalRTestState, withSignalRTestResources } from "./signalr-test-resources.js"
import type {
  OpenCodeRuntime,
  RuntimeCancelRequest,
  RuntimeCancelResult,
  RuntimeFollowupRequest,
  RuntimeFollowupResult,
  RuntimeResult,
} from "../../src/runtime/opencode/index.js"

vi.mock("@microsoft/signalr", () => {
  return {
    HubConnectionBuilder: class {
      private _handlers: Map<string, (...args: unknown[]) => unknown> = new Map()
      private _connection = makeFakeConnection()
      withUrl(_url: string) {
        currentSignalRTestState().builders.push({ handlers: this._handlers, connection: this._connection })
        return this
      }
      withAutomaticReconnect(_reconnectPolicy: number[]) {
        return this
      }
      build() {
        this._connection.on.mockImplementation((event: string, handler: (...args: unknown[]) => unknown) => {
          this._handlers.set(event, handler)
          return this._connection
        })
        return this._connection as unknown as HubConnection
      }
    },
    HubConnectionState: {
      Disconnected: "Disconnected",
      Connecting: "Connecting",
      Connected: "Connected",
      Disconnecting: "Disconnecting",
      Reconnecting: "Reconnecting",
    },
  }
})

type AnyFn = (...args: unknown[]) => unknown

export interface CapturedBuilder {
  handlers: Map<string, (...args: unknown[]) => unknown>
  connection: FakeConnection
}

export interface FakeConnection {
  state: HubConnectionState
  connectionId: string | null
  start: ReturnType<typeof vi.fn>
  stop: ReturnType<typeof vi.fn>
  invoke: ReturnType<typeof vi.fn>
  on: ReturnType<typeof vi.fn>
  onreconnected: ((cb: (id?: string) => void) => void) | undefined
  _reconnectHandler?: (connectionId?: string) => void
}

export interface FakeRuntimeHandles {
  runtime: OpenCodeRuntime
  followupCalls: RuntimeFollowupRequest[]
  setFollowupResult: (result: RuntimeResult<RuntimeFollowupResult>) => void
  setReady: (ready: boolean) => void
}

export interface RecordingOutbox {
  outbox: AgentSessionRuntimeEventOutbox
  records: RuntimeEventRecord[]
  /** Raw `enqueueBeforeExecution` records — used to assert the input guard. */
  beforeExecutionCalls: RuntimeEventRecord[]
  /** Raw `enqueueProducedFact` records — used to assert the produced-fact guard. */
  producedFactCalls: RuntimeEventRecord[]
  /** Pending snapshots kept by the recording filesystem. */
  files: string[]
  /** Bodies written by the recording filesystem (path → JSON). */
  bodies: Map<string, string>
  /** Receipts observed by the recording delivery mock, in order. */
  deliveryReceipts: AgentSessionRuntimeEventReceipt[][]
  /** Trigger next kick once the outbox asks the host to drain. */
  flush: () => Promise<void>
}

function makeFakeConnection(): FakeConnection {
  const conn: FakeConnection = {
    state: HubConnectionState.Disconnected,
    connectionId: null,
    start: vi.fn(),
    stop: vi.fn(),
    invoke: vi.fn(),
    on: vi.fn(),
    onreconnected: undefined,
  }
  conn.start.mockImplementation(async () => {
    conn.state = HubConnectionState.Connected
    conn.connectionId = `conn-${++currentSignalRTestState().nextConnectionId}`
  })
  conn.stop.mockImplementation(async () => {
    conn.state = HubConnectionState.Disconnected
    conn.connectionId = null
  })
  conn.onreconnected = ((cb: (id?: string) => void) => {
    conn._reconnectHandler = cb
  }) as FakeConnection["onreconnected"]
  return conn
}

export function resetBuilders(): void {
  currentSignalRTestState().builders.length = 0
  currentSignalRTestState().nextConnectionId = 0
}

export function lastBuilder(): CapturedBuilder {
  const builder = currentSignalRTestState().builders.at(-1) as CapturedBuilder | undefined
  if (!builder) throw new Error("no captured builder; construct a RunnerSignalRClient first")
  return builder
}

export interface FollowupTestResources {
  readonly resources: Omit<RunnerResourceContext, "fileSystem"> & { fileSystem: RunnerFileSystem }
  readonly runtime: FakeRuntimeHandles
  readonly recording: RecordingOutbox
}

export function followupIt(name: string, body: (context: FollowupTestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources = { fileSystem: new MemoryFileSystem() }
    await withSignalRTestResources(resources, async () => {
      await body({ resources, runtime: makeFakeRuntime(), recording: buildRecordingOutbox() })
    })
  })
}

export function makeFakeRuntime(): FakeRuntimeHandles {
  const followupCalls: RuntimeFollowupRequest[] = []
  let ready = true
  let nextResult: RuntimeResult<RuntimeFollowupResult> = {
    ok: true,
    value: {
      facts: { runtimeSessionId: "ses_runtime", workDir: "/work/project" },
      diagnostics: [],
    },
    diagnostics: [],
  }
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => ready,
    diagnostic: () => null,
    async followup(request: RuntimeFollowupRequest): Promise<RuntimeResult<RuntimeFollowupResult>> {
      followupCalls.push(request)
      return nextResult
    },
    async cancel(_request: RuntimeCancelRequest): Promise<RuntimeResult<RuntimeCancelResult>> {
      return {
        ok: true,
        value: { facts: { runtimeSessionId: "ses_runtime", workDir: "/work/project", cancelled: true }, diagnostics: [] },
        diagnostics: [],
      }
    },
  }
  return {
    runtime: runtime as OpenCodeRuntime,
    followupCalls,
    setFollowupResult(result) { nextResult = result },
    setReady(value) { ready = value },
  }
}

/**
 * Recording in-memory filesystem used by outbox tests. It stores the
 * latest snapshot body per path so tests can drive crash / restart
 * scenarios without touching the Node filesystem adapter.
 */
export class RecordingOutboxFileSystem implements RuntimeEventOutboxFileSystem {
  private readonly textStore = new Map<string, string>()
  /** Set to a thunk to make the next write fail; cleared after firing. */
  public failNextWrite: ((error: Error) => void) | null = null

  async readText(path: string): Promise<string | null> {
    return this.textStore.get(path) ?? null
  }

  async writeAtomicText(path: string, body: string): Promise<void> {
    if (this.failNextWrite) {
      const fail = this.failNextWrite
      this.failNextWrite = null
      fail(new Error("injected write failure"))
    }
    this.textStore.set(path, body)
  }

  body(path: string): string | null {
    return this.textStore.get(path) ?? null
  }
}

export interface BuildOutboxOptions {
  fileSystem?: RuntimeEventOutboxFileSystem
  deliver?: RuntimeEventDelivery
  deliveryDelayMs?: number
  filePath?: string
}

export function buildRecordingOutbox(options: BuildOutboxOptions = {}): RecordingOutbox {
  const beforeExecutionCalls: RuntimeEventRecord[] = []
  const producedFactCalls: RuntimeEventRecord[] = []
  const records: RuntimeEventRecord[] = []
  const deliveryReceipts: AgentSessionRuntimeEventReceipt[][] = []
  const files: string[] = []
  const bodies = new Map<string, string>()
  const deliveryDelayMs = options.deliveryDelayMs ?? 0
  const filePath = options.filePath ?? ".mohist/runner-state/runtime-events.json"
  const fileSystem = options.fileSystem ?? new RecordingOutboxFileSystem()
  const customDeliver = options.deliver
  let idCounter = 0
  const deliver: RuntimeEventDelivery = customDeliver ?? {
    async send(record, signal) {
      if (deliveryDelayMs > 0) {
        await new Promise<void>((resolve, reject) => {
          const timer = setTimeout(() => resolve(), deliveryDelayMs)
          if (signal) {
            signal.addEventListener("abort", () => {
              clearTimeout(timer)
              reject(new Error("aborted"))
            }, { once: true })
          }
        })
      }
      return [{ type: record.event.type }]
    },
  }
  const outbox: AgentSessionRuntimeEventOutbox = {
    ready: () => true,
    async load() {
      // Recording outbox: nothing to load — health is always true.
    },
    async recover() {},
    async enqueueBeforeExecution(record) {
      const internal: RuntimeEventRecord = { ...record }
      beforeExecutionCalls.push(internal)
      records.push(internal)
      const body = JSON.stringify({ version: 1, entries: records.map(serialize) }, null, 2)
      await fileSystem.writeAtomicText(filePath, body)
      files.push(filePath)
      bodies.set(filePath, body)
    },
    async enqueueProducedFact(record) {
      const internal: RuntimeEventRecord = { ...record }
      producedFactCalls.push(internal)
      records.push(internal)
      const body = JSON.stringify({ version: 1, entries: records.map(serialize) }, null, 2)
      await fileSystem.writeAtomicText(filePath, body)
      files.push(filePath)
      bodies.set(filePath, body)
    },
    async enqueueProducedFactBatch(batch) {
      for (const record of batch) {
        const internal: RuntimeEventRecord = { ...record }
        producedFactCalls.push(internal)
        records.push(internal)
      }
      const body = JSON.stringify({ version: 1, entries: records.map(serialize) }, null, 2)
      await fileSystem.writeAtomicText(filePath, body)
      files.push(filePath)
      bodies.set(filePath, body)
    },
    async kick() {
      // Drain one head per call (sequential) using `deliver`.
      while (records.length > 0) {
        const head = records[0]
        const receipts = await deliver.send(head, new AbortController().signal)
        deliveryReceipts.push(receipts)
        if (head.acknowledgementPolicy === "successful-response" || receipts.some((r) => r.type === head.event.type)) {
          records.shift()
          continue
        }
        break
      }
    },
    async stop() {},
    snapshot() {
      return [...records]
    },
  }
  return {
    outbox,
    records: recordsProxy(records),
    beforeExecutionCalls,
    producedFactCalls,
    files,
    bodies,
    deliveryReceipts,
    async flush() {
      await outbox.kick()
    },
  }
  function serialize(record: RuntimeEventRecord) {
    return { ...record, sequence: idCounter++, enqueuedAt: new Date().toISOString() }
  }
}

function recordsProxy(records: RuntimeEventRecord[]): RuntimeEventRecord[] {
  return new Proxy(records, {
    get(target, property) {
      if (property === "length") return records.length
      const value = (target as unknown as Record<PropertyKey, unknown>)[property as number | string]
      return typeof value === "function" ? (value as (...args: unknown[]) => unknown).bind(records) : value
    },
  })
}

export function buildClient(opts: {
  resolver?: AnyFn | null
  outbox?: AgentSessionRuntimeEventOutbox | null
  openCodeRuntime?: OpenCodeRuntime | (() => OpenCodeRuntime | null) | null
  piRuntime?: unknown
  followupOperationJournal?: FollowupOperationJournalStore | null
  cancelOperationJournal?: CancelOperationJournalStore | null
}): RunnerSignalRClient {
  currentSignalRTestState().builders.length = 0
  const resolver = opts.resolver === undefined ? null : opts.resolver
  const outbox = opts.outbox === undefined ? null : opts.outbox
  const openCodeRuntime = opts.openCodeRuntime === undefined ? makeFakeRuntime().runtime : opts.openCodeRuntime
  return new RunnerSignalRClient(
    "https://runner.test",
    "runner-1",
    "/virtual/projects",
    null,
    {
      followupTargetResolver: resolver as never,
      agentSessionRuntimeEventOutbox: outbox,
      openCodeRuntime: openCodeRuntime as never,
      ...(opts.piRuntime !== undefined ? { piRuntime: opts.piRuntime as never } : {}),
      ...(opts.followupOperationJournal !== undefined ? { followupOperationJournal: opts.followupOperationJournal } : {}),
      ...(opts.cancelOperationJournal !== undefined ? { cancelOperationJournal: opts.cancelOperationJournal } : {}),
    },
  )
}

export function emitFollowup(builder: CapturedBuilder, payload: ReceiveFollowupPayload | null | undefined): void {
  const handler = builder.handlers.get("ReceiveFollowup")
  if (!handler) throw new Error("ReceiveFollowup handler was not registered")
  handler(payload)
}

export async function invokeFollowup(builder: CapturedBuilder, payload: ReceiveFollowupPayload | null | undefined): Promise<unknown> {
  const handler = builder.handlers.get("ReceiveFollowup")
  if (!handler) throw new Error("ReceiveFollowup handler was not registered")
  return await handler(payload)
}

export async function flush(): Promise<void> {
  await new Promise((resolve) => setImmediate(resolve))
}

export function genericPayload(text: string): ReceiveFollowupPayload {
  return {
    target: {
      kind: "generic",
      projectId: "proj-1",
      sessionId: "gen-session-1",
      binding: defaultOpenCodeBinding(),
    },
    text,
  }
}

export function workflowPayload(text: string): ReceiveFollowupPayload {
  return {
    target: {
      kind: "workflow",
      projectId: "proj-1",
      workflowRunId: "wr-1",
      sessionName: "work-1",
      binding: defaultOpenCodeBinding(),
    },
    text,
  }
}

export function defaultOpenCodeBinding(): { runtime: "opencode"; runtimeSessionId: string; runnerId: string; workDir: string } {
  return { runtime: "opencode", runtimeSessionId: "runtime-1", runnerId: "runner-1", workDir: "/work/project" }
}

export function defaultPiBinding(): { runtime: "pi"; runtimeSessionId: string; runnerId: string; workDir: string } {
  return { runtime: "pi", runtimeSessionId: "/virtual/sessions/one.jsonl", runnerId: "runner-1", workDir: "/workspace" }
}

export type { AgentSessionRuntimeEventReceipt }
