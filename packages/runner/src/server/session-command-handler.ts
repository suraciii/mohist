import type { SessionCommandJournalStore } from '../runtime/session-command-journal.js'

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
}

export interface SessionCommandResult {
  ok: boolean
  runtimeSessionId?: string
  error?: SessionCommandError
}

export type SessionCommandHandler = (
  request: SessionCommandRequest,
) => Promise<SessionCommandResult> | SessionCommandResult

export type SessionCommandReconciliation =
  | { state: 'completed'; result: SessionCommandResult }
  | { state: 'not-started' }
  | { state: 'indeterminate' }

export type SessionCommandReconciler = (
  request: SessionCommandRequest,
) => Promise<SessionCommandReconciliation> | SessionCommandReconciliation

export interface SessionCommandHandlerDeps {
  handler?: SessionCommandHandler | null
  journal?: SessionCommandJournalStore | null
  reconcileStarted?: SessionCommandReconciler | null
}

export function createSessionCommandHandler(
  deps: SessionCommandHandlerDeps,
): (request: SessionCommandRequest | null | undefined) => Promise<SessionCommandResult> {
  const inFlight = new Map<string, { request: SessionCommandRequest; operation: Promise<SessionCommandResult> }>()
  return async (request: SessionCommandRequest | null | undefined) => {
    const handler = deps.handler
    const journal = deps.journal
    if (!isValidSessionCommandRequest(request) || !handler || !journal) {
      return { ok: false, error: 'unavailable' } satisfies SessionCommandResult
    }

    const key = JSON.stringify([request.sessionId, request.operationId])
    const existing = inFlight.get(key)
    if (existing) {
      if (!sameRequest(existing.request, request)) return unavailable()
      return await existing.operation
    }

    const operation = handleCommand(request, handler, journal, deps.reconcileStarted)
    inFlight.set(key, { request, operation })
    try {
      const result = await operation
      return result
    } catch (error) {
      return { ok: false, error: 'unavailable' } satisfies SessionCommandResult
    } finally {
      if (inFlight.get(key)?.operation === operation) inFlight.delete(key)
    }
  }
}

async function handleCommand(
  request: SessionCommandRequest,
  handler: SessionCommandHandler,
  journal: SessionCommandJournalStore,
  reconcileStarted?: SessionCommandReconciler | null,
): Promise<SessionCommandResult> {
  const existing = await journal.get(request.sessionId, request.operationId)
  if (existing) {
    if (!sameRequest(existing.request, request)) return unavailable()
    if (existing.state === 'completed') return validateResult(request, existing.result!)

    const reconciled = reconcileStarted ? await reconcileStarted(request) : ({ state: 'indeterminate' } as const)
    if (reconciled.state === 'indeterminate') return unavailable()
    if (reconciled.state === 'completed') {
      const result = validateResult(request, reconciled.result)
      if (result.error === 'unavailable') return result
      await journal.complete(request, result)
      return result
    }
  }

  await journal.start(request)
  const result = validateResult(request, await handler(request))
  if (result.error !== 'unavailable') await journal.complete(request, result)
  return result
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
    left.projectId === right.projectId
  )
}

function unavailable(): SessionCommandResult {
  return { ok: false, error: 'unavailable' }
}
