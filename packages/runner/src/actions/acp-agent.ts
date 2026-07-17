import type { ActionContext, ActionResult } from "../core/types.js"
import { resolvePrompt } from "../core/prompt.js"
import { runCommand } from "../system/process.js"
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
  const ok = result.success
  const agentConfig = resolveAgentConfig(context.with)
  const failureCategory = ok ? null : result.failureCategory ?? null
  const failureReason = ok ? null : result.error ?? "ACP agent task failed"
  if (context.ownerKind !== "agent-job" || !context.agentSessionId) {
    await emitSessionEvent(context, "session.closed", { status: ok ? "completed" : "failed", failureReason, failureCategory, exitCode: result.exitCode ?? (ok ? 0 : 1) }, result.acpSessionId ?? null)
  }
  // Completion evaluation (expect files/markers/failIf/_output) is owned
  // by the Workflow task executor; the Action returns its raw turn
  // facts. The boundary between ActionResult (internal) and
  // WorkItemResult (wire) is where completion is applied.
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
    }),
    exitCode: result.exitCode ?? (ok ? 0 : 1),
    turnFact: { finalAssistantText: result.text ?? null },
  }
}
