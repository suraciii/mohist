export type MaintenancePass = (signal: AbortSignal) => Promise<void> | void

export type MaintenanceLifecycleState = 'idle' | 'running' | 'pending' | 'stopping' | 'stopped'

export class MaintenanceLifecycle {
  private state: MaintenanceLifecycleState = 'idle'
  private pending = false
  private currentPass: Promise<void> | null = null
  private currentController: AbortController | null = null
  private stopPromise: Promise<void> | null = null

  public constructor(private readonly operation: MaintenancePass) {}

  public get lifecycleState(): MaintenanceLifecycleState {
    return this.state
  }

  public trigger(): void {
    if (this.state === 'idle') {
      this.startPass()
    } else if (this.state === 'running') {
      this.pending = true
      this.state = 'pending'
    }
  }

  public triggerAndWait(): Promise<void> {
    if (this.state === 'stopping' || this.state === 'stopped') {
      return Promise.reject(new Error('maintenance lifecycle is stopping or stopped'))
    }
    if (this.state === 'idle') {
      return this.startPass()
    }
    if (this.currentPass) {
      return this.currentPass
    }
    return Promise.reject(new Error('maintenance lifecycle has no current pass'))
  }

  public stop(): Promise<void> {
    if (this.stopPromise) return this.stopPromise

    this.state = 'stopping'
    this.pending = false
    this.currentController?.abort()

    const currentPass = this.currentPass
    this.stopPromise = (currentPass ?? Promise.resolve())
      .catch(() => undefined)
      .then(() => {
        this.currentPass = null
        this.currentController = null
        this.state = 'stopped'
      })
    return this.stopPromise
  }

  private startPass(): Promise<void> {
    const controller = new AbortController()
    this.currentController = controller

    let resolvePass!: () => void
    let rejectPass!: (reason?: unknown) => void
    const pass = new Promise<void>((resolve, reject) => {
      resolvePass = resolve
      rejectPass = reject
    })
    this.currentPass = pass
    this.state = 'running'

    try {
      Promise.resolve(this.operation(controller.signal)).then(resolvePass, rejectPass)
    } catch (error) {
      rejectPass(error)
    }

    // Keep fire-and-forget triggers from creating unhandled rejections while
    // preserving the rejection for callers of triggerAndWait().
    void pass.then(
      () => this.completePass(pass),
      () => this.completePass(pass),
    )
    return pass
  }

  private completePass(pass: Promise<void>): void {
    if (this.currentPass !== pass) return

    this.currentPass = null
    this.currentController = null
    if (this.state === 'stopping') return
    if (this.pending) {
      this.pending = false
      this.startPass()
      return
    }
    this.state = 'idle'
  }
}

export function createMaintenanceLifecycle(operation: MaintenancePass): MaintenanceLifecycle {
  return new MaintenanceLifecycle(operation)
}
