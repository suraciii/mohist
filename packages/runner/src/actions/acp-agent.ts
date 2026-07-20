import type { ActionContext, ActionResult } from "../core/types.js"
import { resolvePrompt } from "../core/prompt.js"
import { resolveAgentConfig, buildPromptLoaderContext } from "./acp/agent-config.js"
import { emitSessionEvent } from "./acp/session-events.js"
import { runAcpGenericAgentSession } from "./acp/session-strategies.js"
import { actionErrorMessage, fail, succeed } from "./action-result.js"

export { AcpProcessHandle, setAcpProcessFactoryForTest } from "./acp/process.js"
export type { AcpProcessFactory } from "./acp/process.js"
export { resolveCompactionConfig, defaultCompactionConfig } from "./acp/compaction.js"
export type { CompactionConfig, CompactionStrategy } from "./acp/compaction.js"

/**
 * `mohist/acp-agent` Action — AgentJob-only ACP bridge.
 *
 * The Workflow source no longer routes through here; `mohist/opencode`
 * runs natively through `OpenCodeRuntime` (T-004). The AgentJob path
 * stays on ACP until #410 migrates it.
 */
export async function acpAgentAction(context: ActionContext): Promise<ActionResult> {
  let prompt: string | undefined
  try {
    prompt = await resolvePrompt(context.with?.prompt, buildPromptLoaderContext(context))
  } catch (error) {
    return fail("invalid-input", actionErrorMessage(error))
  }
  if (!prompt?.trim()) return fail("invalid-input", "ACP agent requires 'prompt'")

  const result = await runAcpGenericAgentSession(context, prompt)
  const ok = result.success
  const agentConfig = resolveAgentConfig(context.with)
  const failureCategory = ok ? null : result.failureCategory ?? null
  const failureReason = ok ? null : result.error ?? "ACP agent task failed"
  if (context.ownerKind !== "agent-job" || !context.agentSessionId) {
    await emitSessionEvent(context, "session.closed", { status: ok ? "completed" : "failed", failureReason, failureCategory, exitCode: result.exitCode ?? (ok ? 0 : 1) }, result.acpSessionId ?? null)
  }
  if (!ok) return fail(failureCategory || "agent-failed", failureReason ?? "ACP agent task failed", { exitCode: result.exitCode ?? 1, turnFact: { finalAssistantText: result.text ?? null } })
  return succeed(JSON.stringify({
      kind: "acp-agent",
      status: "success",
      runtimeSessionId: result.acpSessionId,
      model: agentConfig?.model,
      text: result.text,
      providerError: result.providerError,
    }), { exitCode: result.exitCode ?? 0, turnFact: { finalAssistantText: result.text ?? null } })
}
