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
  runtime?: string
}

export type SessionCommandHandler = (
  request: SessionCommandRequest,
) => Promise<SessionCommandResult> | SessionCommandResult

export interface SessionCommandHandlerDeps {
  handler?: SessionCommandHandler | null
}

export function registerSessionCommandHandler(
  conn: signalR.HubConnection,
  deps: SessionCommandHandlerDeps,
): void {
  const resetOperations = new Map<string, Promise<SessionCommandResult>>()
  conn.on("SessionCommand", async (request: SessionCommandRequest | null | undefined) => {
    if (!isSessionCommandRequest(request) || !deps.handler) {
      return { ok: false, error: "unavailable" } satisfies SessionCommandResult
    }

    try {
      if (request.command !== "reset") return await deps.handler(request)

      const operationId = request.operationId!
      const existing = resetOperations.get(operationId)
      if (existing) return await existing

      const result = Promise.resolve(deps.handler(request))
      resetOperations.set(operationId, result)
      return await result
    } catch {
      return { ok: false, error: "unavailable" } satisfies SessionCommandResult
    }
  })
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
      && request.expectedRuntimeSessionId === undefined
      && request.operationId === undefined
  }

  return typeof request.operationId === "string"
    && request.operationId.length > 0
    && request.expectedRuntimeSessionId === request.runtimeSessionId
}
