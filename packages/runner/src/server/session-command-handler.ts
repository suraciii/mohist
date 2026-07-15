import type * as signalR from "@microsoft/signalr"
import type { SessionCommandJournalStore } from "../runtime/session-command-journal.js"

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
  operationId: string
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
  | { state: "completed"; result: SessionCommandResult }
  | { state: "not-started" }
  | { state: "indeterminate" }

export type SessionCommandReconciler = (
  request: SessionCommandRequest,
) => Promise<SessionCommandReconciliation> | SessionCommandReconciliation

export interface SessionCommandHandlerDeps {
  handler?: SessionCommandHandler | null
  journal?: SessionCommandJournalStore | null
  reconcileStarted?: SessionCommandReconciler | null
}

export function registerSessionCommandHandler(
  conn: signalR.HubConnection,
  deps: SessionCommandHandlerDeps,
): void {
  const inFlight = new Map<string, Promise<SessionCommandResult>>()
  conn.on("SessionCommand", async (request: SessionCommandRequest | null | undefined) => {
    const handler = deps.handler
    const journal = deps.journal
    if (!isSessionCommandRequest(request) || !handler || !journal) {
      return { ok: false, error: "unavailable" } satisfies SessionCommandResult
    }

    const key = JSON.stringify([request.sessionId, request.operationId])
    const existing = inFlight.get(key)
    if (existing) return await existing

    const operation = handleCommand(request, handler, journal, deps.reconcileStarted)
    inFlight.set(key, operation)
    try {
      return await operation
    } catch {
      return { ok: false, error: "unavailable" } satisfies SessionCommandResult
    } finally {
      inFlight.delete(key)
    }
  })
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
    if (existing.state === "completed") return existing.result!

    const reconciled = reconcileStarted
      ? await reconcileStarted(request)
      : { state: "indeterminate" } as const
    if (reconciled.state === "indeterminate") return unavailable()
    if (reconciled.state === "completed") {
      const result = validateResult(request, reconciled.result)
      if (result.error === "unavailable") return result
      await journal.complete(request, result)
      return result
    }
  }

  await journal.start(request)
  const result = validateResult(request, await handler(request))
  if (result.error !== "unavailable") await journal.complete(request, result)
  return result
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
    && typeof request.operationId === "string"
    && request.operationId.length > 0
    && isValidCommandBinding(request)
}

function isValidCommandBinding(request: Partial<SessionCommandRequest>): boolean {
  if (request.command === "compact") {
    return request.runtimeSessionId !== null
      && request.expectedRuntimeSessionId == null
      && (request.operationId?.length ?? 0) > 0
  }

  return typeof request.operationId === "string"
    && request.operationId.length > 0
    && request.expectedRuntimeSessionId === request.runtimeSessionId
}

function sameRequest(left: SessionCommandRequest, right: SessionCommandRequest): boolean {
  return left.sessionId === right.sessionId
    && left.runtime === right.runtime
    && left.runtimeSessionId === right.runtimeSessionId
    && left.runnerId === right.runnerId
    && left.workDir === right.workDir
    && left.command === right.command
    && left.expectedRuntimeSessionId === right.expectedRuntimeSessionId
    && left.operationId === right.operationId
}

function unavailable(): SessionCommandResult {
  return { ok: false, error: "unavailable" }
}
