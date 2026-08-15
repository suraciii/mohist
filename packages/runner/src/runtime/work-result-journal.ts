import { dirname, join, resolve } from 'node:path'
import { currentRunnerFileSystem } from '../system/filesystem.js'
import type { DispatchWorkItem, WorkItemResult } from '../core/types.js'
import type { WorkInterruptionFact } from './work-report.js'

export const DEFAULT_WORK_RESULT_JOURNAL_FILE = '.mohist/runner-state/work-results.json'

export type WorkResultJournalState = 'started' | 'completed'

export interface WorkResultJournalEntry {
  work: DispatchWorkItem
  state: WorkResultJournalState
  result?: WorkItemResult
  /** Present when the result was surfaced because a started execution died. */
  interruption?: WorkInterruptionFact
}

export type WorkResultJournalBegin = 'new' | WorkResultJournalState

export type WorkResultJournalPersistence = { state: 'durable' } | { state: 'pending'; error: unknown }

interface WorkResultJournalFile {
  version: 1
  entries: Record<string, WorkResultJournalEntry>
}

/**
 * Durable boundary between physical execution and result delivery. A started
 * entry is a recovery fence: after a process restart it must never be
 * interpreted as permission to execute the dispatch again.
 */
export class WorkResultJournal {
  private readonly filePath: string
  private entries = new Map<string, WorkResultJournalEntry>()
  private loaded = false
  private unavailable = false
  private persistencePending = false
  private writeChain = Promise.resolve()

  constructor(runnerRoot: string, options: { filePath?: string } = {}) {
    this.filePath = options.filePath
      ? resolve(options.filePath)
      : resolve(join(runnerRoot, DEFAULT_WORK_RESULT_JOURNAL_FILE))
  }

  async load(): Promise<void> {
    this.entries.clear()
    this.loaded = false
    this.unavailable = false
    this.persistencePending = false
    try {
      const raw = await currentRunnerFileSystem().readText(this.filePath)
      const file = parseJournal(raw)
      if (!file) {
        this.unavailable = true
        return
      }
      for (const [key, entry] of Object.entries(file.entries)) {
        if (!isEntry(entry) || key !== workKey(entry.work)) {
          this.unavailable = true
          return
        }
        this.entries.set(key, cloneEntry(entry))
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') this.unavailable = true
    } finally {
      this.loaded = true
    }
  }

  ready(): boolean {
    return this.loaded && !this.unavailable && !this.persistencePending
  }

  /** True only while this process retains results that have not been durably written. */
  needsPersistenceRecovery(): boolean {
    return this.loaded && !this.unavailable && this.persistencePending
  }

  disable(): void {
    this.unavailable = true
    this.persistencePending = false
  }

  begin(work: DispatchWorkItem): Promise<WorkResultJournalBegin> {
    return this.mutate(async () => {
      const key = workKey(work)
      const existing = this.entries.get(key)
      if (existing) {
        if (!sameWork(existing.work, work)) throw new Error(`Work result journal identity conflict for ${key}`)
        return existing.state
      }
      this.entries.set(key, { work: cloneWork(work), state: 'started' })
      try {
        await this.persist()
      } catch (error) {
        this.entries.delete(key)
        this.persistencePending = true
        throw error
      }
      return 'new'
    }, true)
  }

  /**
   * Admission for a recovery dispatch. A `started` fence hit by a
   * recovery dispatch is re-armed with the re-rendered payload (the
   * recorded execution died with the process that fenced it; the fence
   * identity — owner + work id — is unchanged, so the payload swap
   * cannot fork execution) and reconciled by the caller instead of
   * refusing replay. A `completed` entry is returned as-is: the
   * awaiting-ack machinery owns reporting that result.
   */
  beginRecovery(work: DispatchWorkItem): Promise<WorkResultJournalBegin> {
    return this.mutate(async () => {
      const key = workKey(work)
      const existing = this.entries.get(key)
      if (existing) {
        if (existing.state === 'completed') return 'completed'
        if (!sameWork(existing.work, work)) {
          const prior = cloneWork(existing.work)
          existing.work = cloneWork(work)
          try {
            await this.persist()
          } catch (error) {
            existing.work = prior
            this.persistencePending = true
            throw error
          }
        }
        return 'started'
      }
      this.entries.set(key, { work: cloneWork(work), state: 'started' })
      try {
        await this.persist()
      } catch (error) {
        this.entries.delete(key)
        this.persistencePending = true
        throw error
      }
      return 'new'
    }, true)
  }

  async complete(work: DispatchWorkItem, result: WorkItemResult): Promise<WorkResultJournalPersistence> {
    return await this.mutate(async () => {
      const key = workKey(work)
      const existing = this.entries.get(key)
      if (!existing || !sameWork(existing.work, work)) {
        throw new Error(`Work result journal cannot complete unknown work ${key}`)
      }
      if (existing.state === 'completed') {
        if (!sameResult(existing.result, result)) throw new Error(`Work result journal result conflict for ${key}`)
        return this.persistencePending ? await this.persistOrRetain() : { state: 'durable' }
      }
      existing.state = 'completed'
      existing.result = cloneResult(result)
      return await this.persistOrRetain()
    })
  }

  /**
   * Retries a prior local persistence failure without making a retained result
   * visible to reporting first. A restarted process cannot call this for a
   * lost in-memory result; its durable started entry remains a recovery fence.
   */
  async retryPendingPersistence(): Promise<WorkResultJournalPersistence> {
    return await this.mutate(async () => {
      if (!this.persistencePending) return { state: 'durable' }
      return await this.persistOrRetain()
    })
  }

  completed(): WorkResultJournalEntry[] {
    this.ensureReady()
    return [...this.entries.values()]
      .filter((entry) => entry.state === 'completed' && entry.result !== undefined)
      .map(cloneEntry)
  }

  started(): WorkResultJournalEntry[] {
    this.ensureReady()
    return [...this.entries.values()].filter((entry) => entry.state === 'started').map(cloneEntry)
  }

  async completeInterrupted(
    work: DispatchWorkItem,
    result: WorkItemResult,
    interruption: WorkInterruptionFact,
  ): Promise<void> {
    await this.mutate(async () => {
      const key = workKey(work)
      const existing = this.entries.get(key)
      if (!existing || !sameWork(existing.work, work)) {
        throw new Error(`Work result journal cannot complete unknown work ${key}`)
      }
      if (existing.state === 'completed') {
        if (!sameResult(existing.result, result) || !sameInterruption(existing.interruption, interruption)) {
          throw new Error(`Work result journal result conflict for ${key}`)
        }
        return
      }
      existing.state = 'completed'
      existing.result = cloneResult(result)
      existing.interruption = structuredClone(interruption)
      try {
        await this.persist()
      } catch (error) {
        this.unavailable = true
        existing.state = 'started'
        delete existing.result
        delete existing.interruption
        throw error
      }
    })
  }

  async acknowledge(work: DispatchWorkItem): Promise<void> {
    await this.mutate(async () => {
      const key = workKey(work)
      const existing = this.entries.get(key)
      if (!existing) return
      if (!sameWork(existing.work, work)) throw new Error(`Work result journal identity conflict for ${key}`)
      if (existing.state !== 'completed')
        throw new Error(`Work result journal cannot acknowledge unfinished work ${key}`)
      this.entries.delete(key)
      try {
        await this.persist()
      } catch (error) {
        this.entries.set(key, existing)
        this.persistencePending = true
        throw error
      }
    }, true)
  }

  /**
   * Retires a recovered execution fence after the owner durably acknowledges
   * a non-terminal unknown-outcome observation. This does not represent a
   * work result and is deliberately separate from completed-result ack.
   */
  async acknowledgeUnconfirmed(work: DispatchWorkItem): Promise<void> {
    await this.mutate(async () => {
      const key = workKey(work)
      const existing = this.entries.get(key)
      if (!existing) return
      if (!sameWork(existing.work, work)) throw new Error(`Work result journal identity conflict for ${key}`)
      if (existing.state !== 'started')
        throw new Error(`Work result journal cannot acknowledge a non-started work ${key}`)
      this.entries.delete(key)
      try {
        await this.persist()
      } catch (error) {
        this.entries.set(key, existing)
        this.persistencePending = true
        throw error
      }
    }, true)
  }

  private async persistOrRetain(): Promise<WorkResultJournalPersistence> {
    try {
      await this.persist()
      this.persistencePending = false
      return { state: 'durable' }
    } catch (error) {
      this.persistencePending = true
      return { state: 'pending', error }
    }
  }

  private async mutate<T>(work: () => Promise<T>, requiresReady = false): Promise<T> {
    const run = this.writeChain.then(
      async () => {
        if (requiresReady) this.ensureReady()
        else this.ensureMutable()
        return await work()
      },
      async () => {
        if (requiresReady) this.ensureReady()
        else this.ensureMutable()
        return await work()
      },
    )
    this.writeChain = run.then(
      () => undefined,
      () => undefined,
    )
    return await run
  }

  private ensureMutable(): void {
    if (!this.loaded || this.unavailable) throw new Error('Work result journal is unavailable')
  }

  private ensureReady(): void {
    this.ensureMutable()
    if (this.persistencePending) throw new Error('Work result journal is unavailable')
  }

  private async persist(): Promise<void> {
    const file: WorkResultJournalFile = {
      version: 1,
      entries: Object.fromEntries([...this.entries.entries()].map(([key, entry]) => [key, cloneEntry(entry)])),
    }
    const directory = dirname(this.filePath)
    await currentRunnerFileSystem().ensureDir(directory)
    const tempPath = `${this.filePath}.tmp`
    await currentRunnerFileSystem().writeText(tempPath, JSON.stringify(file, null, 2))
    await currentRunnerFileSystem().rename(tempPath, this.filePath)
  }
}

export function workKey(work: DispatchWorkItem): string {
  const ownerKind = work.ownerKind === 'agent-job' ? 'agent-job' : 'workflow'
  const ownerId = ownerKind === 'agent-job' ? (work.agentJobId ?? '') : work.workflowRunId
  return `${ownerKind}:${ownerId}:${work.workId}`
}

function parseJournal(raw: string): WorkResultJournalFile | null {
  try {
    const value = JSON.parse(raw) as Partial<WorkResultJournalFile> | null
    return isRecord(value) && value.version === 1 && isRecord(value.entries) ? (value as WorkResultJournalFile) : null
  } catch {
    return null
  }
}

function isEntry(value: unknown): value is WorkResultJournalEntry {
  if (!isRecord(value) || !isWork(value.work)) return false
  if (value.state === 'started') return value.result === undefined && value.interruption === undefined
  return value.state === 'completed' && isResult(value.result) && (value.interruption === undefined || isInterruption(value.interruption))
}

function isWork(value: unknown): value is DispatchWorkItem {
  return (
    isRecord(value) &&
    typeof value.workflowRunId === 'string' &&
    typeof value.workId === 'string' &&
    typeof value.workType === 'string'
  )
}

function isResult(value: unknown): value is WorkItemResult {
  return isRecord(value) && typeof value.status === 'string'
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function sameWork(left: DispatchWorkItem, right: DispatchWorkItem): boolean {
  return JSON.stringify(left) === JSON.stringify(right)
}

function sameResult(left: WorkItemResult | undefined, right: WorkItemResult): boolean {
  return JSON.stringify(left) === JSON.stringify(right)
}

function cloneWork(work: DispatchWorkItem): DispatchWorkItem {
  return structuredClone(work)
}

function cloneResult(result: WorkItemResult): WorkItemResult {
  return structuredClone(result)
}

function cloneEntry(entry: WorkResultJournalEntry): WorkResultJournalEntry {
  return {
    work: cloneWork(entry.work),
    state: entry.state,
    ...(entry.result ? { result: cloneResult(entry.result) } : {}),
    ...(entry.interruption ? { interruption: structuredClone(entry.interruption) } : {}),
  }
}

function isInterruption(value: unknown): value is WorkInterruptionFact {
  return (
    isRecord(value) &&
    value.reason === 'runner-restarted' &&
    typeof value.ownerKind === 'string' &&
    typeof value.ownerId === 'string' &&
    typeof value.workId === 'string' &&
    typeof value.recordedAt === 'string'
  )
}

function sameInterruption(left: WorkInterruptionFact | undefined, right: WorkInterruptionFact): boolean {
  return JSON.stringify(left) === JSON.stringify(right)
}
