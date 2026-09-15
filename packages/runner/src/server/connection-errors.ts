export type RunnerTransportErrorKind = 'cancelled' | 'network' | 'http' | 'protocol'

export interface RunnerTransportErrorOptions {
  operation: string
  kind: RunnerTransportErrorKind
  httpStatus?: number
  serverCode?: string
  safeMessage: string
  cause?: unknown
}

export class RunnerTransportError extends Error {
  readonly operation: string
  readonly kind: RunnerTransportErrorKind
  readonly httpStatus?: number
  readonly serverCode?: string
  readonly safeMessage: string
  readonly cause?: unknown

  constructor(options: RunnerTransportErrorOptions) {
    super(options.safeMessage)
    this.name = 'RunnerTransportError'
    this.operation = options.operation
    this.kind = options.kind
    this.httpStatus = options.httpStatus
    this.serverCode = options.serverCode
    this.safeMessage = options.safeMessage
    if (options.cause !== undefined) this.cause = options.cause
    Object.freeze(this)
  }
}
