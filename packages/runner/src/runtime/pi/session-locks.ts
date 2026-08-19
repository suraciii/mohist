/**
 * Per-session promise mutex for pi session operations. A turn whose prompt
 * never settles would hold its entry forever; `release` lets a caller drop
 * the orphaned entry (the next `run` replaces it, and the orphaned promise
 * is already caught by the chain here).
 */
export class SessionMutexes {
  private readonly locks = new Map<string, Promise<unknown>>()

  run<T>(path: string, operation: () => Promise<T>): Promise<T> {
    const previous = this.locks.get(path)
    if (previous) {
      const settled = previous.catch(() => undefined)
      const current = settled.then(operation)
      this.locks.set(
        path,
        current.catch(() => undefined),
      )
      return current
    }
    const current = operation()
    this.locks.set(
      path,
      current.catch(() => undefined),
    )
    return current
  }

  release(path: string): void {
    this.locks.delete(path)
  }

  clear(): void {
    this.locks.clear()
  }
}
