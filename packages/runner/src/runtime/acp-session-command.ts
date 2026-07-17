import type { ClientSideConnection } from "@agentclientprotocol/sdk"
import type { SessionCommandRequest, SessionCommandResult } from "../server/session-command-handler.js"

export async function executeAcpSessionCommand(
  request: SessionCommandRequest,
  connection: ClientSideConnection | null,
): Promise<SessionCommandResult> {
  if (request.runtime.toLowerCase() !== "opencode") return { ok: false, error: "missing" }
  if (request.command === "reset" && request.expectedRuntimeSessionId !== request.runtimeSessionId) {
    return { ok: false, error: "conflict" }
  }
  if (request.command !== "reset" || !connection || !request.workDir) {
    return { ok: false, error: "notStarted" }
  }

  try {
    const replacement = await connection.newSession({ cwd: request.workDir, mcpServers: [] })
    return replacement.sessionId && replacement.sessionId !== request.runtimeSessionId
      ? { ok: true, runtimeSessionId: replacement.sessionId }
      : { ok: false, error: "unavailable" }
  } catch {
    return { ok: false, error: "unavailable" }
  }
}
