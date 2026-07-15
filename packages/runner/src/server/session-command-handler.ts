import type * as signalR from "@microsoft/signalr"

export type SessionCommand = "compact" | "reset"
export type SessionCommandError = "conflict" | "missing" | "unavailable"

export interface SessionCommandRequest {
  sessionId: string
  runtime: string
  runtimeSessionId: string | null
  runnerId: string
  workDir: string | null
  command: SessionCommand
  expectedRuntimeSessionId?: string | null
  operationId?: string
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

const MAX_RETAINED_RESET_OPERATIONS = 256

interface ResetOperation {
  result: Promise<SessionCommandResult>
  completed: boolean
}

export function registerSessionCommandHandler(
  conn: signalR.HubConnection,
  deps: SessionCommandHandlerDeps,
): void {
  const resetOperations = new Map<string, ResetOperation>()
  conn.on("SessionCommand", async (request: SessionCommandRequest | null | undefined) => {
    if (!isSessionCommandRequest(request) || !deps.handler) {
      return { ok: false, error: "unavailable" } satisfies SessionCommandResult
    }

    try {
      if (request.command !== "reset") return validateResult(request, await deps.handler(request))

      const operationId = request.operationId!
      const existing = resetOperations.get(operationId)
      if (existing) return await existing.result

      discardOldestCompletedResetOperation(resetOperations)
      if (resetOperations.size >= MAX_RETAINED_RESET_OPERATIONS) {
        return { ok: false, error: "unavailable" } satisfies SessionCommandResult
      }

      const operation: ResetOperation = {
        result: Promise.resolve(deps.handler(request)).then((result) => validateResult(request, result)),
        completed: false,
      }
      resetOperations.set(operationId, operation)
      try {
        return await operation.result
      } finally {
        operation.completed = true
      }
    } catch {
      return { ok: false, error: "unavailable" } satisfies SessionCommandResult
    }
  })
}

function discardOldestCompletedResetOperation(operations: Map<string, ResetOperation>): void {
  for (const [operationId, operation] of operations) {
    if (!operation.completed) continue
    operations.delete(operationId)
    return
  }
}

function validateResult(request: SessionCommandRequest, result: SessionCommandResult): SessionCommandResult {
  if (result.ok) {
    const isValid = result.error === undefined && (request.command === "compact"
      ? result.runtimeSessionId === undefined
      : typeof result.runtimeSessionId === "string" && result.runtimeSessionId.length > 0 && result.runtimeSessionId !== request.runtimeSessionId)
    return isValid ? result : { ok: false, error: "unavailable" }
  }

  return result.runtimeSessionId === undefined
    && (result.error === "conflict" || result.error === "missing" || result.error === "unavailable")
    ? result
    : { ok: false, error: "unavailable" }
}

function isSessionCommandRequest(value: unknown): value is SessionCommandRequest {
  if (!value || typeof value !== "object") return false
  const request = value as Partial<SessionCommandRequest>
  return typeof request.sessionId === "string"
    && typeof request.runtime === "string"
    && (typeof request.runtimeSessionId === "string" || request.runtimeSessionId === null)
    && typeof request.runnerId === "string"
    && (typeof request.workDir === "string" || request.workDir === null)
    && (request.command === "compact" || request.command === "reset")
    && (request.expectedRuntimeSessionId === undefined || request.expectedRuntimeSessionId === null || typeof request.expectedRuntimeSessionId === "string")
    && (request.operationId === undefined || typeof request.operationId === "string")
    && isValidCommandBinding(request)
}

function isValidCommandBinding(request: Partial<SessionCommandRequest>): boolean {
  if (request.command === "compact") {
    return request.runtimeSessionId !== null
      && request.expectedRuntimeSessionId == null
      && (request.operationId === undefined || request.operationId.length > 0)
  }

  return typeof request.operationId === "string"
    && request.operationId.length > 0
    && request.expectedRuntimeSessionId === request.runtimeSessionId
}
