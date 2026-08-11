import { dirname, join, resolve } from "node:path"
import { currentRunnerFileSystem } from "../system/filesystem.js"
import type { TaskLogBatch, TaskLogEntry } from "./task-log.js"

export const DEFAULT_TERMINAL_TASK_LOG_DELIVERY_FILE = ".mohist/runner-state/terminal-task-log-deliveries.json"

export type TerminalTaskLogDeliveryState = "pending" | "failed"

export interface TerminalTaskLogDeliveryIdentity {
  ownerKind: "workflow" | "agent-job"
  ownerId: string
  workId: string
}

export interface TerminalTaskLogDeliveryFailure {
  kind: "conflict" | "not-found" | "local"
  status?: number
  code?: string
  message: string
}

export interface TerminalTaskLogDeliveryRecord {
  identity: TerminalTaskLogDeliveryIdentity
  batch: TaskLogBatch
  state: TerminalTaskLogDeliveryState
  failure?: TerminalTaskLogDeliveryFailure
}

export interface TerminalTaskLogDeliveryFileSystem {
  readText(path: string): Promise<string | null>
  writeAtomicText(path: string, body: string): Promise<void>
}

export interface TerminalTaskLogDeliveryStore {
  load(): Promise<void>
  ready(): boolean
  listPending(): Promise<TerminalTaskLogDeliveryRecord[]>
  putPending(record: Omit<TerminalTaskLogDeliveryRecord, "state" | "failure">): Promise<TerminalTaskLogDeliveryRecord>
  acknowledge(identity: TerminalTaskLogDeliveryIdentity): Promise<void>
  markFailed(identity: TerminalTaskLogDeliveryIdentity, failure: TerminalTaskLogDeliveryFailure): Promise<void>
}

interface DeliveryFile {
  version: 1
  deliveries: Record<string, PersistedDeliveryRecord>
}

interface PersistedDeliveryRecord {
  identity: TerminalTaskLogDeliveryIdentity
  batch: {
    entries: PersistedTaskLogEntry[]
    truncated: boolean
  }
  state: TerminalTaskLogDeliveryState
  failure?: TerminalTaskLogDeliveryFailure
}

interface PersistedTaskLogEntry {
  seq: number
  timestamp: string
  source: string
  text: string
}

export class TerminalTaskLogDeliveryStoreImpl implements TerminalTaskLogDeliveryStore {
  private readonly filePath: string
  private deliveries = new Map<string, TerminalTaskLogDeliveryRecord>()
  private loaded = false
  private unavailable = false
  private writeChain = Promise.resolve()

  constructor(
    runnerRoot: string,
    options: {
      filePath?: string
      fileSystem?: TerminalTaskLogDeliveryFileSystem
    } = {},
  ) {
    this.filePath = resolve(options.filePath ?? join(runnerRoot, DEFAULT_TERMINAL_TASK_LOG_DELIVERY_FILE))
    this.fileSystem = options.fileSystem ?? new NodeTerminalTaskLogDeliveryFileSystem()
  }

  private readonly fileSystem: TerminalTaskLogDeliveryFileSystem

  async load(): Promise<void> {
    this.deliveries = new Map()
    this.unavailable = false
    try {
      const raw = await this.fileSystem.readText(this.filePath)
      if (raw === null) return
      const file = parseFile(raw)
      if (!file) {
        this.unavailable = true
        return
      }
      for (const [key, persisted] of Object.entries(file.deliveries)) {
        const record = parseRecord(persisted)
        if (!record || deliveryKey(record.identity) !== key) {
          this.unavailable = true
          return
        }
        this.deliveries.set(key, record)
      }
    } catch {
      this.unavailable = true
    } finally {
      this.loaded = true
    }
  }

  ready(): boolean {
    return this.loaded && !this.unavailable
  }

  async listPending(): Promise<TerminalTaskLogDeliveryRecord[]> {
    this.ensureAvailable()
    return [...this.deliveries.values()]
      .filter((record) => record.state === "pending")
      .map(cloneRecord)
  }

  async putPending(record: Omit<TerminalTaskLogDeliveryRecord, "state" | "failure">): Promise<TerminalTaskLogDeliveryRecord> {
    return await this.mutate(async () => {
      const key = deliveryKey(record.identity)
      const existing = this.deliveries.get(key)
      if (existing) {
        if (!sameSnapshot(existing, record)) {
          throw new Error(`Terminal task-log payload changed for ${key}`)
        }
        return cloneRecord(existing)
      }

      const pending: TerminalTaskLogDeliveryRecord = {
        identity: { ...record.identity },
        batch: cloneBatch(record.batch),
        state: "pending",
      }
      this.deliveries.set(key, pending)
      try {
        await this.persist()
      } catch (error) {
        this.deliveries.delete(key)
        throw error
      }
      return cloneRecord(pending)
    })
  }

  async acknowledge(identity: TerminalTaskLogDeliveryIdentity): Promise<void> {
    await this.mutate(async () => {
      const key = deliveryKey(identity)
      const existing = this.deliveries.get(key)
      if (!existing || existing.state !== "pending") return
      this.deliveries.delete(key)
      try {
        await this.persist()
      } catch (error) {
        this.deliveries.set(key, existing)
        throw error
      }
    })
  }

  async markFailed(identity: TerminalTaskLogDeliveryIdentity, failure: TerminalTaskLogDeliveryFailure): Promise<void> {
    await this.mutate(async () => {
      const key = deliveryKey(identity)
      const existing = this.deliveries.get(key)
      if (!existing || existing.state !== "pending") return
      existing.state = "failed"
      existing.failure = { ...failure }
      try {
        await this.persist()
      } catch (error) {
        existing.state = "pending"
        delete existing.failure
        throw error
      }
    })
  }

  private async mutate<T>(operation: () => Promise<T>): Promise<T> {
    this.ensureAvailable()
    const run = this.writeChain.then(operation, operation)
    this.writeChain = run.then(() => undefined, () => undefined)
    return await run
  }

  private ensureAvailable(): void {
    if (!this.loaded) throw new Error("Terminal task-log delivery store has not been loaded")
    if (this.unavailable) throw new Error("Terminal task-log delivery store is unavailable")
  }

  private async persist(): Promise<void> {
    const file: DeliveryFile = {
      version: 1,
      deliveries: Object.fromEntries(
        [...this.deliveries].map(([key, record]) => [key, toPersistedRecord(record)]),
      ),
    }
    await this.fileSystem.writeAtomicText(this.filePath, JSON.stringify(file, null, 2))
  }
}

export class NodeTerminalTaskLogDeliveryFileSystem implements TerminalTaskLogDeliveryFileSystem {
  async readText(path: string): Promise<string | null> {
    try {
      return await currentRunnerFileSystem().readText(path)
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return null
      throw error
    }
  }

  async writeAtomicText(path: string, body: string): Promise<void> {
    const fileSystem = currentRunnerFileSystem()
    await fileSystem.ensureDir(dirname(path))
    const temporary = `${path}.tmp`
    await fileSystem.writeText(temporary, body)
    await fileSystem.rename(temporary, path)
  }
}

export function deliveryKey(identity: TerminalTaskLogDeliveryIdentity): string {
  return `${identity.ownerKind}:${identity.ownerId}:${identity.workId}`
}

function parseFile(raw: string): DeliveryFile | null {
  try {
    const value = JSON.parse(raw) as Partial<DeliveryFile> | null
    if (!value || value.version !== 1 || !isRecord(value.deliveries)) return null
    return value as DeliveryFile
  } catch {
    return null
  }
}

function parseRecord(value: unknown): TerminalTaskLogDeliveryRecord | null {
  if (!isRecord(value)) return null
  const identity = parseIdentity(value.identity)
  const batch = parseBatch(value.batch)
  const state = value.state
  if (!identity || !batch || (state !== "pending" && state !== "failed")) return null
  const failure = value.failure === undefined ? undefined : parseFailure(value.failure)
  if (state === "failed" && !failure) return null
  if (state === "pending" && value.failure !== undefined) return null
  return { identity, batch, state, ...(failure ? { failure } : {}) }
}

function parseIdentity(value: unknown): TerminalTaskLogDeliveryIdentity | null {
  if (!isRecord(value)) return null
  const ownerKind = value.ownerKind
  const ownerId = value.ownerId
  const workId = value.workId
  if ((ownerKind !== "workflow" && ownerKind !== "agent-job")
    || typeof ownerId !== "string" || ownerId.length === 0
    || typeof workId !== "string" || workId.length === 0) return null
  return { ownerKind, ownerId, workId }
}

function parseBatch(value: unknown): TaskLogBatch | null {
  if (!isRecord(value) || typeof value.truncated !== "boolean" || !Array.isArray(value.entries)) return null
  const entries: TaskLogEntry[] = []
  let previous = 0
  for (const item of value.entries) {
    if (!isRecord(item)
      || typeof item.seq !== "number" || !Number.isSafeInteger(item.seq) || item.seq <= previous
      || typeof item.timestamp !== "string" || Number.isNaN(Date.parse(item.timestamp))
      || typeof item.source !== "string" || item.source.length === 0
      || typeof item.text !== "string") return null
    entries.push({ seq: item.seq, timestamp: new Date(item.timestamp), source: item.source, text: item.text })
    previous = item.seq
  }
  return { entries, truncated: value.truncated }
}

function parseFailure(value: unknown): TerminalTaskLogDeliveryFailure | null {
  if (!isRecord(value)
    || (value.kind !== "conflict" && value.kind !== "not-found" && value.kind !== "local")
    || typeof value.message !== "string" || value.message.length === 0) return null
  if (value.status !== undefined && (typeof value.status !== "number" || !Number.isSafeInteger(value.status))) return null
  if (value.code !== undefined && typeof value.code !== "string") return null
  return {
    kind: value.kind,
    ...(value.status === undefined ? {} : { status: value.status }),
    ...(value.code === undefined ? {} : { code: value.code }),
    message: value.message,
  }
}

function toPersistedRecord(record: TerminalTaskLogDeliveryRecord): PersistedDeliveryRecord {
  return {
    identity: { ...record.identity },
    batch: {
      entries: record.batch.entries.map((entry) => ({
        seq: entry.seq,
        timestamp: entry.timestamp.toISOString(),
        source: entry.source,
        text: entry.text,
      })),
      truncated: record.batch.truncated,
    },
    state: record.state,
    ...(record.failure ? { failure: { ...record.failure } } : {}),
  }
}

function sameSnapshot(left: Pick<TerminalTaskLogDeliveryRecord, "identity" | "batch">, right: Pick<TerminalTaskLogDeliveryRecord, "identity" | "batch">): boolean {
  return deliveryKey(left.identity) === deliveryKey(right.identity)
    && left.batch.truncated === right.batch.truncated
    && left.batch.entries.length === right.batch.entries.length
    && left.batch.entries.every((entry, index) => {
      const other = right.batch.entries[index]
      return other !== undefined
        && entry.seq === other.seq
        && entry.timestamp.getTime() === other.timestamp.getTime()
        && entry.source === other.source
        && entry.text === other.text
    })
}

function cloneBatch(batch: TaskLogBatch): TaskLogBatch {
  return {
    entries: batch.entries.map((entry) => ({ ...entry, timestamp: new Date(entry.timestamp.getTime()) })),
    truncated: batch.truncated,
  }
}

function cloneRecord(record: TerminalTaskLogDeliveryRecord): TerminalTaskLogDeliveryRecord {
  return {
    identity: { ...record.identity },
    batch: cloneBatch(record.batch),
    state: record.state,
    ...(record.failure ? { failure: { ...record.failure } } : {}),
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value)
}
