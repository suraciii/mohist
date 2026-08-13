import { dirname, join, resolve } from "node:path"
import { currentRunnerFileSystem } from "../system/filesystem.js"
import type { CancelAgentSessionPayload, CancelAgentSessionReply } from "../server/session-target.js"

export const DEFAULT_CANCEL_OPERATION_JOURNAL_FILE = ".mohist/runner-state/cancel-operations.json"

export interface CancelOperationJournalEntry {
  request: CancelAgentSessionPayload
  state: "started" | "completed"
  reply?: CancelAgentSessionReply
}

export interface CancelOperationJournalStore {
  load(): Promise<void>
  get(sessionId: string, operationId: string): Promise<CancelOperationJournalEntry | null>
  start(sessionId: string, payload: CancelAgentSessionPayload): Promise<CancelOperationJournalEntry>
  complete(sessionId: string, payload: CancelAgentSessionPayload, reply: CancelAgentSessionReply): Promise<void>
}

export interface CancelOperationJournalFileSystem {
  readText(path: string): Promise<string | null>
  writeAtomicText(path: string, body: string): Promise<void>
  rename(source: string, destination: string): Promise<void>
}

interface JournalFile {
  version: 1
  operations: Record<string, Record<string, CancelOperationJournalEntry>>
}

export class CancelOperationJournal implements CancelOperationJournalStore {
  private readonly filePath: string
  private operations = new Map<string, Map<string, CancelOperationJournalEntry>>()
  private loaded = false
  private unavailable = false
  private writeChain = Promise.resolve()

  constructor(
    runnerRoot: string,
    filePath = join(runnerRoot, DEFAULT_CANCEL_OPERATION_JOURNAL_FILE),
    private readonly fileSystem: CancelOperationJournalFileSystem = new NodeCancelOperationJournalFileSystem(),
  ) {
    this.filePath = resolve(filePath)
  }

  async load(): Promise<void> {
    this.operations = new Map()
    this.unavailable = false
    try {
      const raw = await this.fileSystem.readText(this.filePath)
      if (raw === null) return
      const file = parse(raw)
      if (!file) {
        await this.quarantine()
        return
      }
      for (const [sessionId, values] of Object.entries(file.operations)) {
        const entries = new Map<string, CancelOperationJournalEntry>()
        for (const [operationId, entry] of Object.entries(values)) {
          if (!isEntry(entry) || entry.request.sessionId !== sessionId || entry.request.operationId !== operationId) {
            await this.quarantine()
            return
          }
          entries.set(operationId, clone(entry))
        }
        this.operations.set(sessionId, entries)
      }
    } catch {
      await this.quarantine()
    } finally {
      this.loaded = true
    }
  }

  private async quarantine(): Promise<void> {
    this.operations = new Map()
    try {
      await this.fileSystem.rename(this.filePath, `${this.filePath}.corrupt`)
    } catch {
      // Corrupt state is still discarded in memory if quarantine cannot finish.
    }
  }

  async get(sessionId: string, operationId: string): Promise<CancelOperationJournalEntry | null> {
    this.ensureAvailable()
    const entry = this.operations.get(sessionId)?.get(operationId)
    return entry ? clone(entry) : null
  }

  async start(sessionId: string, payload: CancelAgentSessionPayload): Promise<CancelOperationJournalEntry> {
    return await this.mutate(async () => {
      const operationId = requireOperationId(payload)
      const existing = this.operations.get(sessionId)?.get(operationId)
      if (existing) return clone(existing)
      const entry: CancelOperationJournalEntry = { request: clonePayload(payload), state: "started" }
      const entries = this.operations.get(sessionId) ?? new Map<string, CancelOperationJournalEntry>()
      entries.set(operationId, entry)
      this.operations.set(sessionId, entries)
      await this.persist()
      return clone(entry)
    })
  }

  async complete(sessionId: string, payload: CancelAgentSessionPayload, reply: CancelAgentSessionReply): Promise<void> {
    await this.mutate(async () => {
      const operationId = requireOperationId(payload)
      const entry = this.operations.get(sessionId)?.get(operationId)
      if (!entry || !samePayload(entry.request, payload)) throw new Error("Cancel operation journal cannot complete an unknown operation")
      entry.state = "completed"
      entry.reply = { ...reply }
      await this.persist()
    })
  }

  private async mutate<T>(work: () => Promise<T>): Promise<T> {
    this.ensureAvailable()
    const run = this.writeChain.then(work, work)
    this.writeChain = run.then(() => undefined, () => undefined)
    return await run
  }

  private ensureAvailable(): void {
    if (!this.loaded) throw new Error("Cancel operation journal has not been loaded")
    if (this.unavailable) throw new Error("Cancel operation journal is unavailable")
  }

  private async persist(): Promise<void> {
    const file: JournalFile = {
      version: 1,
      operations: Object.fromEntries([...this.operations].map(([sessionId, entries]) => [
        sessionId,
        Object.fromEntries([...entries].map(([operationId, entry]) => [operationId, clone(entry)])),
      ])),
    }
    await this.fileSystem.writeAtomicText(this.filePath, JSON.stringify(file))
  }
}

export class NodeCancelOperationJournalFileSystem implements CancelOperationJournalFileSystem {
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

  async rename(source: string, destination: string): Promise<void> {
    await currentRunnerFileSystem().rename(source, destination)
  }
}

function parse(raw: string): JournalFile | null {
  try {
    const value = JSON.parse(raw) as Partial<JournalFile> | null
    return value?.version === 1 && value.operations && typeof value.operations === "object" && !Array.isArray(value.operations)
      ? value as JournalFile
      : null
  } catch {
    return null
  }
}

function isEntry(value: unknown): value is CancelOperationJournalEntry {
  if (!value || typeof value !== "object") return false
  const entry = value as Partial<CancelOperationJournalEntry>
  return (entry.state === "started" || entry.state === "completed")
    && isPayload(entry.request)
    && (entry.state !== "completed" || isReply(entry.reply))
}

function isPayload(value: unknown): value is CancelAgentSessionPayload {
  if (!value || typeof value !== "object") return false
  const payload = value as Partial<CancelAgentSessionPayload>
  return typeof payload.sessionId === "string" && payload.sessionId.length > 0
    && typeof payload.operationId === "string" && payload.operationId.length > 0
    && typeof payload.turnId === "string" && payload.turnId.length > 0
    && payload.target !== undefined
}

function isReply(value: unknown): value is CancelAgentSessionReply {
  return !!value && typeof value === "object" && typeof (value as CancelAgentSessionReply).state === "string"
}

function requireOperationId(payload: CancelAgentSessionPayload): string {
  if (!payload.operationId) throw new Error("Cancel operation requires an operation id")
  return payload.operationId
}

function samePayload(left: CancelAgentSessionPayload, right: CancelAgentSessionPayload): boolean {
  return left.sessionId === right.sessionId
    && left.operationId === right.operationId
    && left.turnId === right.turnId
    && JSON.stringify(left.target) === JSON.stringify(right.target)
}

function clonePayload(payload: CancelAgentSessionPayload): CancelAgentSessionPayload {
  return JSON.parse(JSON.stringify(payload)) as CancelAgentSessionPayload
}

function clone(entry: CancelOperationJournalEntry): CancelOperationJournalEntry {
  return { request: clonePayload(entry.request), state: entry.state, ...(entry.reply ? { reply: { ...entry.reply } } : {}) }
}
