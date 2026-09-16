/** Serializes complete session operations, including their bounded closeout. */
export class SessionMutexes {
  private readonly locks = new Map<string, Promise<unknown>>()

  run<T>(path: string, operation: () => Promise<T>): Promise<T> {
    const previous = this.locks.get(path)
    const current = previous ? previous.catch(() => undefined).then(operation) : operation()
    const tracked = current.catch(() => undefined)
    this.locks.set(path, tracked)
    void tracked.then(() => {
      if (this.locks.get(path) === tracked) this.locks.delete(path)
    })
    return current
  }

  clear(): void {
    this.locks.clear()
  }
}
