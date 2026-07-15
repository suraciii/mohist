import type { ActionContext, ActionResult } from "../core/types.js"
import { resolvePrompt } from "../core/prompt.js"
import { runCommand } from "../system/process.js"
import { promiseValue, verifyExpectations } from "./expectations.js"
import { resolveAgentConfig, buildPromptLoaderContext } from "./acp/agent-config.js"
import { emitSessionEvent } from "./acp/session-events.js"
import { runAcpWorkflowAgentSession } from "./acp/session-strategies.js"

export { AcpProcessHandle, setAcpProcessFactoryForTest } from "./acp/process.js"
export type { AcpProcessFactory } from "./acp/process.js"
export { resolveCompactionConfig, defaultCompactionConfig } from "./acp/compaction.js"
export type { CompactionConfig, CompactionStrategy } from "./acp/compaction.js"

async function restoreAgentToolNoise(context: ActionContext) {
  const log = context.log
  const lineOptions = log ? { onLine: (line: string) => log.write("action:acp-agent", line) } : undefined
  for (const path of [".opencode/package-lock.json", ".opencode/bun.lock", ".opencode/node_modules/.package-lock.json"]) {
    try {
      await runCommand("git", ["checkout", "--", path], context.workDir, context.signal, undefined, lineOptions)
    } catch {
      // Tool-noise cleanup must never turn a successful agent run into a failure.
    }
  }
}

export async function acpAgentAction(context: ActionContext): Promise<ActionResult> {
  let prompt: string | undefined
  try {
    prompt = await resolvePrompt(context.with?.prompt, buildPromptLoaderContext(context))
  } catch (error) {
    return { status: "failure", message: error instanceof Error ? error.message : String(error) }
  }
  if (!prompt?.trim()) return { status: "failure", message: "ACP agent requires 'prompt'" }

  const result = await runAcpWorkflowAgentSession(context, prompt)
  await restoreAgentToolNoise(context)
  const verification = result.expectation ?? await verifyExpectations(context)
  const failIfMatch = verification.failIfMatches[0]
  const failIfTriggered = !!failIfMatch
  const ok = result.success && verification.satisfied && !failIfTriggered
  const agentConfig = resolveAgentConfig(context.with)
  const failureCategory = ok ? null : result.failureCategory ?? null
  const promise = promiseValue(verification.matched)
  const failureReason = ok ? null : failIfTriggered
    ? `failIf marker matched: ${failIfMatch.marker}`
    : result.error ?? verification.message
  if (context.ownerKind !== "agent-job" || !context.agentSessionId || !ok) {
    await emitSessionEvent(context, "session.closed", { status: ok ? "completed" : "failed", failureReason, failureCategory, exitCode: result.exitCode ?? (ok ? 0 : 1) })
  }
  return {
    status: ok ? "success" : "failure",
    message: ok ? "ACP agent task completed" : failureReason,
    output: JSON.stringify({
      kind: "acp-agent",
      status: ok ? "success" : "failure",
      runtimeSessionId: result.acpSessionId,
      model: agentConfig?.model,
      text: result.text,
      error: result.error,
      providerError: result.providerError,
      expectation: verification,
      promise,
      failIfMarker: failIfTriggered ? failIfMatch.marker : null,
    }),
    exitCode: result.exitCode ?? (ok ? 0 : 1),
  }
}
