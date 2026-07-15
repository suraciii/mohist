import type * as signalR from "@microsoft/signalr"

export type SessionCommand = "compact" | "reset"
export type SessionCommandError = "conflict" | "missing" | "unavailable"

export interface SessionCommandRequest {
  sessionId: string
  runtime: string
  runtimeSessionId: string
  runnerId: string
  workDir: string | null
  command: SessionCommand
  expectedRuntimeSessionId?: string
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

export function registerSessionCommandHandler(
  conn: signalR.HubConnection,
  deps: SessionCommandHandlerDeps,
): void {
  conn.on("SessionCommand", async (request: SessionCommandRequest | null | undefined) => {
    if (!isSessionCommandRequest(request) || !deps.handler) {
      return { ok: false, error: "unavailable" } satisfies SessionCommandResult
    }

    try {
      return await deps.handler(request)
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
    && typeof request.runtimeSessionId === "string"
    && typeof request.runnerId === "string"
    && (typeof request.workDir === "string" || request.workDir === null)
    && (request.command === "compact" || request.command === "reset")
    && (request.expectedRuntimeSessionId === undefined || typeof request.expectedRuntimeSessionId === "string")
}
