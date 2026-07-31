import { resolve } from "node:path"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type { RuntimeDiagnostic } from "./types.js"
import type { WorkspaceRemovalFenceResult } from "../workspace-removal-fence.js"

export type DirectoryReleaseOutcome = "untracked" | "disposed" | "busy" | "failed"

export interface DirectoryReleaseResult {
  readonly directory: string
  readonly outcome: DirectoryReleaseOutcome
  readonly diagnostic?: RuntimeDiagnostic
}

export interface DirectoryReclaimResult {
  readonly tracked: number
  readonly candidates: number
  readonly disposed: number
  readonly busy: number
  readonly failed: number
  readonly blockedDirectories: readonly string[]
  readonly diagnostics: readonly RuntimeDiagnostic[]
}

export interface DirectoryOperationLease {
  markUsed(): void
  trackPending(promise: Promise<unknown>): Promise<void>
}

interface DirectoryEntry {
  readonly generation: number
  admitted: number
  used: boolean
  disposing: boolean
  removing: boolean
  generationInvalidated: boolean
  readonly waiters: Array<() => void>
  pendingDispose: Promise<DirectoryReleaseResult> | null
  pendingOperations: Set<Promise<unknown>>
}

interface StatusSnapshot {
  readonly kind: "idle" | "busy" | "invalid"
  readonly diagnostic?: RuntimeDiagnostic
}

export class OpenCodeDirectoryInstances {
  private generation = 0
  private readonly entries = new Map<string, DirectoryEntry>()

  constructor(private readonly client: () => OpencodeClient | null) {}

  async withOperation<T>(directory: string, operation: (lease: DirectoryOperationLease) => Promise<T>): Promise<T> {
    const key = resolve(directory)
    const entry = await this.admit(key)
    const lease: DirectoryOperationLease = {
      markUsed: () => {
        entry.used = true
      },
      trackPending: (promise) => {
        let resolveSettled!: () => void
        const settled = new Promise<void>((resolve) => {
          resolveSettled = resolve
        })
        entry.pendingOperations.add(promise)
        void promise.then(() => {
          this.finishPendingOperation(key, entry, promise)
          resolveSettled()
        }, () => {
          this.finishPendingOperation(key, entry, promise)
          resolveSettled()
        })
        return settled
      },
    }

    try {
      return await operation(lease)
    } finally {
      this.finishOperation(key, entry)
    }
  }

  async reclaimWhere(predicate: (directory: string) => boolean): Promise<DirectoryReclaimResult> {
    const trackedDirectories = [...this.entries.entries()]
      .filter(([, entry]) => entry.used)
      .map(([directory]) => directory)
    const candidates = trackedDirectories.filter(predicate)
    const results = await Promise.all(candidates.map((directory) => this.release(directory)))
    const diagnostics = results.flatMap((result) => result.diagnostic ? [result.diagnostic] : [])
    const disposed = results.filter((result) => result.outcome === "disposed").length
    const busy = results.filter((result) => result.outcome === "busy").length
    const failed = results.filter((result) => result.outcome === "failed").length
    return {
      tracked: trackedDirectories.length,
      candidates: candidates.length,
      disposed,
      busy,
      failed,
      blockedDirectories: results
        .filter((result) => result.outcome === "busy" || result.outcome === "failed")
        .map((result) => result.directory),
      diagnostics,
    }
  }

  async withRemovalFence<T>(directory: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
    const key = resolve(directory)
    let entry = this.entries.get(key)
    if (entry && (entry.removing || entry.disposing || entry.pendingDispose || entry.admitted > 0 || entry.pendingOperations.size > 0)) {
      return { kind: "busy" }
    }
    if (!entry) {
      entry = this.newEntry()
      this.entries.set(key, entry)
    }

    entry.removing = true
    entry.disposing = true
    const pending = this.prepareRemoval(key, entry)
    entry.pendingDispose = pending
    const release = await pending
    entry.pendingDispose = null
    if (entry.generationInvalidated || this.entries.get(key) !== entry) {
      this.completeRemoval(key, entry)
      return { kind: "busy" }
    }
    if (entry.used && release.outcome !== "disposed") {
      this.retainAfterRemovalFailure(entry)
      return release.outcome === "busy" ? { kind: "busy" } : { kind: "failed" }
    }

    try {
      const value = await callback()
      return { kind: "completed", value }
    } catch {
      return { kind: "failed" }
    } finally {
      this.completeRemoval(key, entry)
    }
  }

  async release(directory: string): Promise<DirectoryReleaseResult> {
    const key = resolve(directory)
    const entry = this.entries.get(key)
    if (!entry) return { directory: key, outcome: "untracked" }
    if (entry.disposing || entry.pendingDispose) return { directory: key, outcome: "busy" }
    if (entry.admitted > 0 || entry.pendingOperations.size > 0) return { directory: key, outcome: "busy" }

    entry.disposing = true
    const pending = this.dispose(key, entry)
    entry.pendingDispose = pending
    return await pending
  }

  resetGeneration(): void {
    const oldEntries = [...this.entries.values()]
    this.generation += 1
    for (const [directory, entry] of [...this.entries.entries()]) {
      if (entry.removing) {
        entry.generationInvalidated = true
        continue
      }
      this.entries.delete(directory)
    }
    for (const entry of oldEntries.filter((candidate) => !candidate.removing)) {
      for (const waiter of entry.waiters.splice(0)) waiter()
    }
  }

  private async admit(directory: string): Promise<DirectoryEntry> {
    while (true) {
      let entry = this.entries.get(directory)
      if (!entry) {
        entry = this.newEntry()
        this.entries.set(directory, entry)
      }
      if (!entry.disposing && !entry.removing) {
        entry.admitted += 1
        return entry
      }
      await new Promise<void>((resolveWaiter) => entry!.waiters.push(resolveWaiter))
    }
  }

  private finishOperation(directory: string, entry: DirectoryEntry): void {
    entry.admitted -= 1
    if (entry.admitted !== 0 || entry.pendingOperations.size !== 0) return
    if (!entry.used) {
      if (this.entries.get(directory) === entry) this.entries.delete(directory)
      return
    }
    this.finishEntry(directory, entry)
  }

  private finishPendingOperation(directory: string, entry: DirectoryEntry, promise: Promise<unknown>): void {
    entry.pendingOperations.delete(promise)
    if (entry.admitted === 0 && entry.pendingOperations.size === 0) this.finishEntry(directory, entry)
  }

  private finishEntry(directory: string, entry: DirectoryEntry): void {
    if (this.entries.get(directory) !== entry || entry.generation !== this.generation) return
    for (const waiter of entry.waiters.splice(0)) waiter()
  }

  private async dispose(directory: string, entry: DirectoryEntry): Promise<DirectoryReleaseResult> {
    const client = this.client()
    if (!client) {
      const result = this.failure(directory, "opencode-runtime-unavailable", "OpenCode client is unavailable while releasing a directory Instance")
      this.completeDispose(directory, entry, result)
      return result
    }

    let result: DirectoryReleaseResult
    try {
      const statuses = await client.session.status({ directory }, { throwOnError: true })
      const snapshot = readStatusSnapshot(statuses?.data)
      if (snapshot.kind === "busy") {
        result = { directory, outcome: "busy", diagnostic: snapshot.diagnostic }
      } else if (snapshot.kind === "invalid") {
        result = { directory, outcome: "failed", diagnostic: snapshot.diagnostic }
      } else {
        const disposed = await client.instance.dispose({ directory }, { throwOnError: true })
        if (disposed?.data !== true) {
          result = this.failure(directory, "opencode-instance-dispose-unconfirmed", "OpenCode Instance disposal was not confirmed")
        } else {
          result = { directory, outcome: "disposed" }
        }
      }
    } catch (cause) {
      result = this.failure(directory, "opencode-instance-release-failed", errorMessage(cause, "OpenCode directory Instance release failed"))
    }
    this.completeDispose(directory, entry, result)
    return result
  }

  private async prepareRemoval(directory: string, entry: DirectoryEntry): Promise<DirectoryReleaseResult> {
    if (!entry.used) return { directory, outcome: "untracked" }
    const client = this.client()
    if (!client) return this.failure(directory, "opencode-runtime-unavailable", "OpenCode client is unavailable while removing a workspace")
    try {
      const statuses = await client.session.status({ directory }, { throwOnError: true })
      const snapshot = readStatusSnapshot(statuses?.data)
      if (snapshot.kind === "busy") return { directory, outcome: "busy", diagnostic: snapshot.diagnostic }
      if (snapshot.kind === "invalid") return { directory, outcome: "failed", diagnostic: snapshot.diagnostic }
      if (entry.generationInvalidated) return { directory, outcome: "busy" }
      const disposed = await client.instance.dispose({ directory }, { throwOnError: true })
      if (disposed?.data !== true) return this.failure(directory, "opencode-instance-dispose-unconfirmed", "OpenCode Instance disposal was not confirmed")
      return { directory, outcome: "disposed" }
    } catch (cause) {
      return this.failure(directory, "opencode-instance-release-failed", errorMessage(cause, "OpenCode directory Instance release failed"))
    }
  }

  private newEntry(): DirectoryEntry {
    return {
      generation: this.generation,
      admitted: 0,
      used: false,
      disposing: false,
      removing: false,
      generationInvalidated: false,
      waiters: [],
      pendingDispose: null,
      pendingOperations: new Set(),
    }
  }

  private completeDispose(directory: string, entry: DirectoryEntry, result: DirectoryReleaseResult): void {
    if (this.entries.get(directory) !== entry || entry.generation !== this.generation) return
    entry.disposing = false
    entry.pendingDispose = null
    if (result.outcome === "disposed") {
      this.entries.delete(directory)
      for (const waiter of entry.waiters.splice(0)) waiter()
      return
    }
    for (const waiter of entry.waiters.splice(0)) waiter()
  }

  private completeRemoval(directory: string, entry: DirectoryEntry): void {
    if (this.entries.get(directory) !== entry) return
    entry.removing = false
    entry.disposing = false
    entry.pendingDispose = null
    this.entries.delete(directory)
    for (const waiter of entry.waiters.splice(0)) waiter()
  }

  private retainAfterRemovalFailure(entry: DirectoryEntry): void {
    entry.removing = false
    entry.disposing = false
    entry.pendingDispose = null
    for (const waiter of entry.waiters.splice(0)) waiter()
  }

  private failure(directory: string, code: string, message: string): DirectoryReleaseResult {
    return {
      directory,
      outcome: "failed",
      diagnostic: { severity: "warning", code, message },
    }
  }
}

function readStatusSnapshot(data: unknown): StatusSnapshot {
  if (!data || typeof data !== "object" || Array.isArray(data)) {
    return { kind: "invalid", diagnostic: statusDiagnostic("OpenCode session.status returned a malformed status map") }
  }

  for (const value of Object.values(data)) {
    if (!value || typeof value !== "object" || Array.isArray(value) || typeof (value as { type?: unknown }).type !== "string") {
      return { kind: "invalid", diagnostic: statusDiagnostic("OpenCode session.status returned a malformed status entry") }
    }
    const type = (value as { type: string }).type
    if (type === "idle") continue
    if (type === "busy" || type === "retry") {
      return { kind: "busy", diagnostic: { severity: "info", code: "opencode-instance-busy", message: `OpenCode session.status returned ${type}` } }
    }
    return { kind: "invalid", diagnostic: statusDiagnostic(`OpenCode session.status returned unknown status ${type}`) }
  }
  return { kind: "idle" }
}

function statusDiagnostic(message: string): RuntimeDiagnostic {
  return { severity: "warning", code: "opencode-session-status-invalid", message }
}

function errorMessage(cause: unknown, fallback: string): string {
  if (cause instanceof Error) return cause.message || fallback
  return String(cause) || fallback
}
