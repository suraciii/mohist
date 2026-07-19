import { mkdir, readFile, rename, writeFile } from "node:fs/promises"
import { dirname, join, resolve } from "node:path"
import type { SessionTarget } from "./session-target.js"
import type { ServerConnection } from "./connection.js"

// Issue-410 T-003 / design D3: the followup failure outbox is now a
// pure Mohist-owned delivery primitive. It branches on `target.kind`
// only (workflow → `workflowAgentSessionRuntimeEvents`; generic →
// `agentSessionRuntimeEvents`) and never references a live
// `ClientSideConnection` or any other ACP surface — the runtime
// session id is the record's own `runtimeSessionId` field, resolved
// out-of-band by the runner host when the original followup was
// attempted. Persisted entries remain queryable across restarts;
// legacy ACP-bound entries still flow through the same kind branch
// (the legacy binding is no longer consulted at delivery time).

const DEFAULT_FOLLOWUP_FAILURE_OUTBOX_FILE = ".mohist/runner-state/followup-failures.json"
const RETRY_DELAY_MS = 2_000
const DELIVERY_TIMEOUT_MS = 5_000

export interface FollowupFailureRecord {
  operationId: string
  target: SessionTarget
  runtimeSessionId: string
  status: "completed" | "failed"
  error: string | null
  completedAt: string
}

interface FollowupFailureOutboxFile {
  version: 1
  entries: FollowupFailureRecord[]
}

export interface FollowupFailureOutboxStore {
  load(): Promise<void>
  record(record: FollowupFailureRecord, server: ServerConnection): Promise<void>
  drain(server: ServerConnection): Promise<void>
}

export class FollowupFailureOutbox implements FollowupFailureOutboxStore {
  private readonly filePath: string
  private readonly entries = new Map<string, FollowupFailureRecord>()
  private loaded = false
  private unavailable = false
  private draining: Promise<void> | null = null
  private retryTimer: ReturnType<typeof setTimeout> | null = null
  private writeChain = Promise.resolve()

  constructor(runnerRoot: string, filePath?: string) {
    this.filePath = filePath ? resolve(filePath) : resolve(join(runnerRoot, DEFAULT_FOLLOWUP_FAILURE_OUTBOX_FILE))
  }

  async load(): Promise<void> {
    this.entries.clear()
    this.unavailable = false
    try {
      const raw = await readFile(this.filePath, "utf8")
      const parsed = parseOutbox(raw)
      if (!parsed) {
        this.unavailable = true
        return
      }
      for (const entry of parsed.entries) this.entries.set(entry.operationId, entry)
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") this.unavailable = true
    } finally {
      this.loaded = true
    }
  }

  async record(record: FollowupFailureRecord, server: ServerConnection): Promise<void> {
    await this.mutate(async () => {
      this.entries.set(record.operationId, cloneRecord(record))
      await this.persist()
    })
    await this.drain(server)
  }

  async drain(server: ServerConnection): Promise<void> {
    this.ensureAvailable()
    if (this.draining) return await this.draining
    this.draining = this.mutate(() => this.drainEntries(server))
    try {
      await this.draining
    } finally {
      this.draining = null
    }
  }

  private async drainEntries(server: ServerConnection): Promise<void> {
    for (const entry of this.entries.values()) {
      try {
        await publishFailure(server, entry)
      } catch {
        this.scheduleRetry(server)
        return
      }
      this.entries.delete(entry.operationId)
      await this.persist()
    }
  }

  private scheduleRetry(server: ServerConnection): void {
    if (this.retryTimer || this.entries.size === 0) return
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null
      void this.drain(server).catch(() => {})
    }, RETRY_DELAY_MS)
    this.retryTimer.unref?.()
  }

  private async mutate<T>(work: () => Promise<T>): Promise<T> {
    this.ensureAvailable()
    const run = this.writeChain.then(work, work)
    this.writeChain = run.then(() => undefined, () => undefined)
    return await run
  }

  private ensureAvailable(): void {
    if (!this.loaded) throw new Error("Follow-up failure outbox has not been loaded")
    if (this.unavailable) throw new Error("Follow-up failure outbox is unavailable")
  }

  private async persist(): Promise<void> {
    const body: FollowupFailureOutboxFile = {
      version: 1,
      entries: [...this.entries.values()].map(cloneRecord),
    }
    await mkdir(dirname(this.filePath), { recursive: true })
    const temporary = `${this.filePath}.${process.pid}.${Date.now()}.tmp`
    await writeFile(temporary, JSON.stringify(body, null, 2))
    await rename(temporary, this.filePath)
  }
}

async function publishFailure(server: ServerConnection, entry: FollowupFailureRecord): Promise<void> {
  const body = {
    workId: null,
    workType: null,
    stage: null,
    runtimeSessionId: entry.runtimeSessionId,
    runtimeEvents: [
      {
          type: entry.status === "failed" ? "session.followup_failed" : "session.followup_completed",
          payload: {
          status: entry.status,
          ...(entry.error ? { failureReason: entry.error } : {}),
          source: "followup",
          operationId: entry.operationId,
          runtimeSessionId: entry.runtimeSessionId,
          completedAt: entry.completedAt,
        },
      },
    ],
  }
  const controller = new AbortController()
  let rejectTimeout!: (error: Error) => void
  const timeout = new Promise<never>((_, reject) => { rejectTimeout = reject })
  const timer = setTimeout(() => {
    controller.abort()
    rejectTimeout(new Error("follow-up terminal delivery timed out"))
  }, DELIVERY_TIMEOUT_MS)
  timer.unref?.()
  try {
    if (entry.target.kind === "workflow") {
      await Promise.race([server.workflowAgentSessionRuntimeEvents(
        entry.target.projectId,
        entry.target.workflowRunId,
        entry.target.sessionName,
        body,
        controller.signal,
      ), timeout])
      return
    }
    await Promise.race([server.agentSessionRuntimeEvents(
      entry.target.projectId, entry.target.sessionId, body, controller.signal), timeout])
  } finally {
    clearTimeout(timer)
  }
}

function parseOutbox(raw: string): FollowupFailureOutboxFile | null {
  try {
    const value = JSON.parse(raw) as unknown
    if (!isRecord(value) || value.version !== 1 || !Array.isArray(value.entries)) return null
    const entries = value.entries.filter(isRecord).map(toRecord)
    return entries.length === value.entries.length ? { version: 1, entries } : null
  } catch {
    return null
  }
}

function toRecord(value: Record<string, unknown>): FollowupFailureRecord {
  if (typeof value.operationId !== "string"
    || typeof value.runtimeSessionId !== "string"
    || (value.status !== "completed" && value.status !== "failed")
    || (value.error !== null && typeof value.error !== "string")
    || typeof value.completedAt !== "string"
    || !isSessionTarget(value.target)) {
    throw new Error("Invalid follow-up failure outbox entry")
  }
  return {
    operationId: value.operationId,
      target: value.target,
      runtimeSessionId: value.runtimeSessionId,
      status: value.status,
      error: value.error,
      completedAt: value.completedAt,
  }
}

function isSessionTarget(value: unknown): value is SessionTarget {
  if (!isRecord(value) || typeof value.projectId !== "string") return false
  if (value.kind === "workflow") return typeof value.workflowRunId === "string" && typeof value.sessionName === "string"
  return value.kind === "generic" && typeof value.sessionId === "string"
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value)
}

function cloneRecord(record: FollowupFailureRecord): FollowupFailureRecord {
  return {
    ...record,
    target: { ...record.target },
  }
}
