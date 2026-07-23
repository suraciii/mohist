import { mkdir, readFile, rename, writeFile } from "node:fs/promises"
import { dirname, join, resolve } from "node:path"

export const DEFAULT_FOLLOWUP_OPERATION_JOURNAL_FILE = ".mohist/runner-state/followup-operations.json"

export type FollowupOperationState = "claimed" | "submitted"
export type FollowupOperationClaim = "new" | FollowupOperationState

export interface FollowupOperationJournalStore {
  load(): Promise<void>
  claim(sessionKey: string, operationId: string): Promise<FollowupOperationClaim>
  markSubmitted(sessionKey: string, operationId: string): Promise<void>
  release(sessionKey: string, operationId: string): Promise<void>
}

export class FollowupOperationJournal implements FollowupOperationJournalStore {
  private readonly filePath: string
  private readonly operations = new Map<string, FollowupOperationState>()
  private loaded = false
  private unavailable = false
  private writeChain = Promise.resolve()

  constructor(runnerRoot: string, options: { filePath?: string } = {}) {
    this.filePath = options.filePath
      ? resolve(options.filePath)
      : resolve(join(runnerRoot, DEFAULT_FOLLOWUP_OPERATION_JOURNAL_FILE))
  }

  async load(): Promise<void> {
    this.operations.clear()
    try {
      const raw = await readFile(this.filePath, "utf8")
      const value = JSON.parse(raw) as { version?: unknown; operations?: unknown }
      if (value?.version !== 2 || !isRecord(value.operations)) {
        this.unavailable = true
        return
      }
      for (const [operation, state] of Object.entries(value.operations)) {
        if (state !== "claimed" && state !== "submitted") {
          this.unavailable = true
          return
        }
        this.operations.set(operation, state)
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") this.unavailable = true
    } finally {
      this.loaded = true
    }
  }

  async claim(sessionKey: string, operationId: string): Promise<FollowupOperationClaim> {
    return await this.mutate(async () => {
      const key = operationKey(sessionKey, operationId)
      const existing = this.operations.get(key)
      if (existing) return existing
      this.operations.set(key, "claimed")
      try {
        await this.persist()
      } catch (error) {
        this.operations.delete(key)
        throw error
      }
      return "new"
    })
  }

  async markSubmitted(sessionKey: string, operationId: string): Promise<void> {
    await this.mutate(async () => {
      const key = operationKey(sessionKey, operationId)
      const existing = this.operations.get(key)
      if (existing === "submitted") return
      if (existing !== "claimed") throw new Error("Follow-up operation was not claimed")
      this.operations.set(key, "submitted")
      try {
        await this.persist()
      } catch (error) {
        this.operations.set(key, "claimed")
        throw error
      }
    })
  }

  async release(sessionKey: string, operationId: string): Promise<void> {
    await this.mutate(async () => {
      const key = operationKey(sessionKey, operationId)
      const existing = this.operations.get(key)
      if (existing !== "claimed") return
      this.operations.delete(key)
      try {
        await this.persist()
      } catch (error) {
        this.operations.set(key, existing)
        throw error
      }
    })
  }

  private async mutate<T>(work: () => Promise<T>): Promise<T> {
    this.ensureAvailable()
    const run = this.writeChain.then(work, work)
    this.writeChain = run.then(() => undefined, () => undefined)
    return await run
  }

  private ensureAvailable(): void {
    if (!this.loaded || this.unavailable) throw new Error("Follow-up operation journal is unavailable")
  }

  private async persist(): Promise<void> {
    const directory = dirname(this.filePath)
    await mkdir(directory, { recursive: true })
    const tempPath = `${this.filePath}.${process.pid}.${Date.now()}.tmp`
    await writeFile(tempPath, JSON.stringify({ version: 2, operations: Object.fromEntries(this.operations) }, null, 2))
    await rename(tempPath, this.filePath)
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value)
}

function operationKey(sessionKey: string, operationId: string): string {
  return `${sessionKey}\u0000${operationId}`
}
