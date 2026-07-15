import { mkdir, readFile, rename, writeFile } from "node:fs/promises"
import { dirname, join, resolve } from "node:path"
import type { SessionCommandRequest, SessionCommandResult } from "../server/session-command-handler.js"

export const DEFAULT_SESSION_COMMAND_JOURNAL_FILE = ".mohist/runner-state/session-commands.json"

export interface SessionCommandJournalEntry {
  request: SessionCommandRequest
  state: "started" | "completed"
  result?: SessionCommandResult
}

export interface SessionCommandJournalStore {
  load(): Promise<void>
  get(sessionId: string, operationId: string): Promise<SessionCommandJournalEntry | null>
  start(request: SessionCommandRequest): Promise<SessionCommandJournalEntry>
  complete(request: SessionCommandRequest, result: SessionCommandResult): Promise<void>
}

interface SessionCommandJournalFile {
  version: 1
  operations: Record<string, Record<string, SessionCommandJournalEntry>>
}

export interface SessionCommandJournalOptions {
  filePath?: string
}

export class SessionCommandJournal implements SessionCommandJournalStore {
  private readonly filePath: string
  private operations = new Map<string, Map<string, SessionCommandJournalEntry>>()
  private loaded = false
  private unavailable = false
  private writeChain = Promise.resolve()

  constructor(runnerRoot: string, options: SessionCommandJournalOptions = {}) {
    this.filePath = options.filePath
      ? resolve(options.filePath)
      : resolve(join(runnerRoot, DEFAULT_SESSION_COMMAND_JOURNAL_FILE))
  }

  async load(): Promise<void> {
    this.operations = new Map()
    try {
      const raw = await readFile(this.filePath, "utf8")
      const file = parseJournal(raw)
      if (!file) {
        this.unavailable = true
        return
      }
      for (const [sessionId, entries] of Object.entries(file.operations)) {
        const operations = new Map<string, SessionCommandJournalEntry>()
        for (const [operationId, entry] of Object.entries(entries)) {
          if (!isEntry(entry)
            || entry.request.sessionId !== sessionId
            || entry.request.operationId !== operationId) {
            this.unavailable = true
            return
          }
          operations.set(operationId, cloneEntry(entry))
        }
        this.operations.set(sessionId, operations)
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") this.unavailable = true
    } finally {
      this.loaded = true
    }
  }

  async get(sessionId: string, operationId: string): Promise<SessionCommandJournalEntry | null> {
    this.ensureAvailable()
    const entry = this.operations.get(sessionId)?.get(operationId)
    return entry ? cloneEntry(entry) : null
  }

  async start(request: SessionCommandRequest): Promise<SessionCommandJournalEntry> {
    return await this.mutate(async () => {
      const existing = this.operations.get(request.sessionId)?.get(request.operationId)
      if (existing) return cloneEntry(existing)

      const entry: SessionCommandJournalEntry = {
        request: { ...request },
        state: "started",
      }
      const entries = this.operations.get(request.sessionId) ?? new Map<string, SessionCommandJournalEntry>()
      entries.set(request.operationId, entry)
      this.operations.set(request.sessionId, entries)
      await this.persist()
      return cloneEntry(entry)
    })
  }

  async complete(request: SessionCommandRequest, result: SessionCommandResult): Promise<void> {
    await this.mutate(async () => {
      const entry = this.operations.get(request.sessionId)?.get(request.operationId)
      if (!entry || !sameRequest(entry.request, request)) {
        throw new Error("Session command journal cannot complete an unknown operation")
      }
      entry.state = "completed"
      entry.result = { ...result }
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
    if (!this.loaded) throw new Error("Session command journal has not been loaded")
    if (this.unavailable) throw new Error("Session command journal is unavailable")
  }

  private async persist(): Promise<void> {
    const file: SessionCommandJournalFile = {
      version: 1,
      operations: Object.fromEntries(
        [...this.operations.entries()].map(([sessionId, entries]) => [
          sessionId,
          Object.fromEntries([...entries.entries()].map(([operationId, entry]) => [operationId, cloneEntry(entry)])),
        ]),
      ),
    }
    const directory = dirname(this.filePath)
    await mkdir(directory, { recursive: true })
    const tempPath = `${this.filePath}.${process.pid}.${Date.now()}.tmp`
    await writeFile(tempPath, JSON.stringify(file, null, 2))
    await rename(tempPath, this.filePath)
  }
}

function parseJournal(raw: string): SessionCommandJournalFile | null {
  try {
    const value = JSON.parse(raw) as Partial<SessionCommandJournalFile> | null
    return value
      && typeof value === "object"
      && value.version === 1
      && value.operations
      && typeof value.operations === "object"
      ? value as SessionCommandJournalFile
      : null
  } catch {
    return null
  }
}

function isEntry(value: unknown): value is SessionCommandJournalEntry {
  if (!value || typeof value !== "object") return false
  const entry = value as Partial<SessionCommandJournalEntry>
  return (entry.state === "started" || entry.state === "completed")
    && isRequest(entry.request)
    && (entry.state !== "completed" || isResult(entry.result))
}

function isRequest(value: unknown): value is SessionCommandRequest {
  if (!value || typeof value !== "object") return false
  const request = value as Partial<SessionCommandRequest>
  return typeof request.sessionId === "string"
    && typeof request.runtime === "string"
    && (typeof request.runtimeSessionId === "string" || request.runtimeSessionId === null)
    && typeof request.runnerId === "string"
    && (typeof request.workDir === "string" || request.workDir === null)
    && (request.command === "compact" || request.command === "reset")
    && (request.expectedRuntimeSessionId === undefined || request.expectedRuntimeSessionId === null || typeof request.expectedRuntimeSessionId === "string")
    && typeof request.operationId === "string"
    && request.operationId.length > 0
}

function isResult(value: unknown): value is SessionCommandResult {
  if (!value || typeof value !== "object") return false
  const result = value as Partial<SessionCommandResult>
  return typeof result.ok === "boolean"
    && (result.runtimeSessionId === undefined || typeof result.runtimeSessionId === "string")
    && (result.error === undefined || result.error === "conflict" || result.error === "missing" || result.error === "unavailable")
}

function sameRequest(left: SessionCommandRequest, right: SessionCommandRequest): boolean {
  return left.sessionId === right.sessionId
    && left.runtime === right.runtime
    && left.runtimeSessionId === right.runtimeSessionId
    && left.runnerId === right.runnerId
    && left.workDir === right.workDir
    && left.command === right.command
    && left.expectedRuntimeSessionId === right.expectedRuntimeSessionId
    && left.operationId === right.operationId
}

function cloneEntry(entry: SessionCommandJournalEntry): SessionCommandJournalEntry {
  return {
    request: { ...entry.request },
    state: entry.state,
    ...(entry.result ? { result: { ...entry.result } } : {}),
  }
}
