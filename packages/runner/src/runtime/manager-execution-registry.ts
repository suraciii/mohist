import type { CommandRuntimeHandle } from '../server/command-runtime.js'
import type { ManagerExecutionBoundary } from './manager-execution-boundary.js'

export interface ManagerExecutionEntry {
  readonly executionId: string
  readonly boundary: ManagerExecutionBoundary
  readonly handle?: CommandRuntimeHandle
  readonly sessionId: string
  readonly runtimeSessionId: string
  readonly workDir: string
}

export type ManagerExecutionRuntimeBinding = Pick<
  ManagerExecutionEntry,
  'handle' | 'sessionId' | 'runtimeSessionId' | 'workDir'
> & { readonly handle: CommandRuntimeHandle }

/**
 * Process-local ownership for every Manager boundary, including control
 * channel follow-ups. The registry is deliberately non-durable: an epoch
 * change or host shutdown destroys all entries instead of attempting to
 * recover a bearer or runtime in place.
 */
export class ManagerExecutionRegistry {
  private readonly entries = new Map<string, ManagerExecutionEntry>()

  register(entry: ManagerExecutionEntry): void {
    this.entries.set(entry.executionId, entry)
  }

  bindRuntime(boundary: ManagerExecutionBoundary, binding: ManagerExecutionRuntimeBinding): boolean {
    for (const [executionId, entry] of this.entries) {
      if (entry.boundary !== boundary) continue
      this.entries.set(executionId, { ...entry, ...binding })
      return true
    }
    return false
  }

  remove(boundary: ManagerExecutionBoundary): boolean {
    let removed = false
    for (const [executionId, entry] of this.entries) {
      if (entry.boundary !== boundary) continue
      this.entries.delete(executionId)
      removed = true
    }
    return removed
  }

  findForCancel(sessionId: string, runtime: string, runtimeSessionId: string): ManagerExecutionEntry | null {
    const normalizedRuntime = runtime.toLowerCase()
    return (
      [...this.entries.values()].find(
        (entry) =>
          entry.sessionId === sessionId &&
          entry.runtimeSessionId === runtimeSessionId &&
          entry.handle?.kind === normalizedRuntime,
      ) ?? null
    )
  }

  async dispose(boundary: ManagerExecutionBoundary): Promise<void> {
    if (!this.remove(boundary)) return
    await boundary.dispose()
  }

  async disposeAll(): Promise<void> {
    const entries = [...this.entries.values()]
    this.entries.clear()
    await Promise.allSettled([...new Set(entries.map((entry) => entry.boundary))].map((boundary) => boundary.dispose()))
  }
}
