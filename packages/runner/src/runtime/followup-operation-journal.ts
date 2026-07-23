import { mkdir, readFile, rename, writeFile } from "node:fs/promises"
import { dirname, join, resolve } from "node:path"

export const DEFAULT_FOLLOWUP_OPERATION_JOURNAL_FILE = ".mohist/runner-state/followup-operations.json"

export interface FollowupOperationJournalStore {
  load(): Promise<void>
  claim(sessionKey: string, operationId: string): Promise<boolean>
  release(sessionKey: string, operationId: string): Promise<void>
}

export class FollowupOperationJournal implements FollowupOperationJournalStore {
  private readonly filePath: string
  private readonly operations = new Set<string>()
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
      if (value?.version !== 1 || !Array.isArray(value.operations) || value.operations.some((item) => typeof item !== "string")) {
        this.unavailable = true
        return
      }
      for (const operation of value.operations) this.operations.add(operation)
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") this.unavailable = true
    } finally {
      this.loaded = true
    }
  }

  async claim(sessionKey: string, operationId: string): Promise<boolean> {
    return await this.mutate(async () => {
      const key = operationKey(sessionKey, operationId)
      if (this.operations.has(key)) return false
      this.operations.add(key)
      await this.persist()
      return true
    })
  }

  async release(sessionKey: string, operationId: string): Promise<void> {
    await this.mutate(async () => {
      this.operations.delete(operationKey(sessionKey, operationId))
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
    if (!this.loaded || this.unavailable) throw new Error("Follow-up operation journal is unavailable")
  }

  private async persist(): Promise<void> {
    const directory = dirname(this.filePath)
    await mkdir(directory, { recursive: true })
    const tempPath = `${this.filePath}.${process.pid}.${Date.now()}.tmp`
    await writeFile(tempPath, JSON.stringify({ version: 1, operations: [...this.operations] }, null, 2))
    await rename(tempPath, this.filePath)
  }
}

function operationKey(sessionKey: string, operationId: string): string {
  return `${sessionKey}\u0000${operationId}`
}
