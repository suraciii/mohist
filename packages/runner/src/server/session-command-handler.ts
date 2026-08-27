export type SessionCommand = 'compact' | 'reset'
export type SessionCommandError = 'conflict' | 'missing' | 'notStarted' | 'unavailable'

export interface SessionCommandRequest {
  sessionId: string
  runtime: string
  runtimeSessionId: string | null
  runnerId: string
  workDir: string | null
  command: SessionCommand
  expectedRuntimeSessionId?: string | null
  operationId: string
  projectId?: string | null
  processGeneration: string
}

export interface SessionCommandResult {
  ok: boolean
  runtimeSessionId?: string
  error?: SessionCommandError
}

export type SessionCommandHandler = (
  request: SessionCommandRequest,
) => Promise<SessionCommandResult> | SessionCommandResult

export interface SessionCommandHandlerDeps {
  handler?: SessionCommandHandler | null
}

export function createSessionCommandHandler(
  deps: SessionCommandHandlerDeps,
): (request: SessionCommandRequest | null | undefined) => Promise<SessionCommandResult> {
  const inFlight = new Map<string, { request: SessionCommandRequest; operation: Promise<SessionCommandResult> }>()
  return async (request: SessionCommandRequest | null | undefined) => {
    const handler = deps.handler
    if (!isValidSessionCommandRequest(request) || !handler) return unavailable()

    const key = JSON.stringify([request.sessionId, request.operationId])
    const existing = inFlight.get(key)
    if (existing) {
      if (!sameRequest(existing.request, request)) return unavailable()
      return await existing.operation
    }

    const operation = Promise.resolve(handler(request)).then((result) => validateResult(request, result))
    inFlight.set(key, { request, operation })
    try {
      return await operation
    } catch {
      return unavailable()
    } finally {
      if (inFlight.get(key)?.operation === operation) inFlight.delete(key)
    }
  }
}

export function validateResult(request: SessionCommandRequest, result: SessionCommandResult): SessionCommandResult {
  return isValidSessionCommandResult(request, result) ? result : unavailable()
}

export function isValidSessionCommandResult(
  request: SessionCommandRequest,
  result: unknown,
): result is SessionCommandResult {
  if (!result || typeof result !== 'object') return false
  const candidate = result as Partial<SessionCommandResult>
  if (candidate.ok === true) {
    return (
      candidate.error === undefined &&
      (request.command === 'compact'
        ? candidate.runtimeSessionId === undefined
        : typeof candidate.runtimeSessionId === 'string' &&
          candidate.runtimeSessionId.length > 0 &&
          candidate.runtimeSessionId !== request.runtimeSessionId)
    )
  }

  return (
    candidate.ok === false &&
    candidate.runtimeSessionId === undefined &&
    (candidate.error === 'conflict' ||
      candidate.error === 'missing' ||
      candidate.error === 'notStarted' ||
      candidate.error === 'unavailable')
  )
}

export function isValidSessionCommandRequest(value: unknown): value is SessionCommandRequest {
  if (!value || typeof value !== 'object') return false
  const request = value as Partial<SessionCommandRequest>
  return (
    typeof request.sessionId === 'string' &&
    typeof request.runtime === 'string' &&
    (typeof request.runtimeSessionId === 'string' || request.runtimeSessionId === null) &&
    typeof request.runnerId === 'string' &&
    (typeof request.workDir === 'string' || request.workDir === null) &&
    (request.command === 'compact' || request.command === 'reset') &&
    (request.expectedRuntimeSessionId === undefined ||
      request.expectedRuntimeSessionId === null ||
      typeof request.expectedRuntimeSessionId === 'string') &&
    typeof request.operationId === 'string' &&
    request.operationId.length > 0 &&
    typeof request.processGeneration === 'string' &&
    request.processGeneration.length > 0 &&
    (request.projectId === undefined || request.projectId === null || typeof request.projectId === 'string') &&
    isValidCommandBinding(request)
  )
}

function isValidCommandBinding(request: Partial<SessionCommandRequest>): boolean {
  if (request.command === 'compact') {
    return (
      request.runtimeSessionId !== null &&
      request.expectedRuntimeSessionId == null &&
      (request.operationId?.length ?? 0) > 0
    )
  }

  return (
    typeof request.operationId === 'string' &&
    request.operationId.length > 0 &&
    request.expectedRuntimeSessionId === request.runtimeSessionId
  )
}

function sameRequest(left: SessionCommandRequest, right: SessionCommandRequest): boolean {
  return (
    left.sessionId === right.sessionId &&
    left.runtime === right.runtime &&
    left.runtimeSessionId === right.runtimeSessionId &&
    left.runnerId === right.runnerId &&
    left.workDir === right.workDir &&
    left.command === right.command &&
    left.expectedRuntimeSessionId === right.expectedRuntimeSessionId &&
    left.operationId === right.operationId &&
    left.projectId === right.projectId &&
    left.processGeneration === right.processGeneration
  )
}

function unavailable(): SessionCommandResult {
  return { ok: false, error: 'unavailable' }
}
