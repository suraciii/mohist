export type RunnerTransportErrorKind = 'cancelled' | 'network' | 'http' | 'protocol'

export const RUNNER_REENROLL_ACTION = "re-run 'mo install runner'"

const MAX_DIAGNOSTIC_MESSAGE_LENGTH = 512

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

export interface RunnerTransportDiagnosticOptions {
  readonly includeCredentialGuidance?: boolean
}

export function runnerTransportDiagnostics(
  error: unknown,
  options: RunnerTransportDiagnosticOptions = {},
): Record<string, unknown> {
  if (!(error instanceof RunnerTransportError)) return { exception: error }
  return {
    operation: error.operation,
    kind: error.kind,
    ...(error.httpStatus === undefined ? {} : { httpStatus: error.httpStatus }),
    ...(error.serverCode === undefined ? {} : { serverCode: error.serverCode }),
    safeMessage: error.safeMessage,
    ...(options.includeCredentialGuidance && isConfirmedRunnerCredentialRejection(error)
      ? { nextAction: RUNNER_REENROLL_ACTION }
      : {}),
    exception: error,
  }
}

export function isConfirmedRunnerCredentialRejection(error: unknown): error is RunnerTransportError {
  return (
    error instanceof RunnerTransportError &&
    error.kind === 'http' &&
    (error.httpStatus === 401 || error.httpStatus === 403)
  )
}

export function withRunnerEnrollmentGuidance(error: RunnerTransportError): RunnerTransportError {
  if (!isConfirmedRunnerCredentialRejection(error) || error.safeMessage.includes(RUNNER_REENROLL_ACTION)) return error
  const suffix = `; ${RUNNER_REENROLL_ACTION}`
  const available = MAX_DIAGNOSTIC_MESSAGE_LENGTH - suffix.length
  const prefix =
    error.safeMessage.length <= available ? error.safeMessage : `${error.safeMessage.slice(0, available - 3)}...`
  return new RunnerTransportError({
    operation: error.operation,
    kind: error.kind,
    ...(error.httpStatus === undefined ? {} : { httpStatus: error.httpStatus }),
    ...(error.serverCode === undefined ? {} : { serverCode: error.serverCode }),
    safeMessage: `${prefix}${suffix}`,
    ...(error.cause === undefined ? {} : { cause: error.cause }),
  })
}
