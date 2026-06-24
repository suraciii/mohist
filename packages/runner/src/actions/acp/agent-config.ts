import type { ActionContext, JsonObject } from "../../core/types.js"
import { numberInput, objectInput, stringInput } from "../../core/json.js"
import type { PromptLoaderContext } from "../../core/prompt.js"
import type { CompactionConfig } from "./compaction.js"
import { resolveCompactionConfigFromInput } from "./compaction.js"

export interface AgentConfig {
  model?: string
  timeoutMs?: number
  sessionStartTimeoutMs?: number
  livenessQuietThresholdMs?: number
  probeTimeoutMs?: number
  compaction?: CompactionConfig
}

export function resolveAgentConfig(with_?: JsonObject | null): AgentConfig | undefined {
  if (!with_) return undefined
  const agent = objectInput(with_, "agent")
  if (agent && typeof agent === "object") {
    return {
      model: stringInput(agent as JsonObject, "model") ?? undefined,
      timeoutMs: numberInput(agent as JsonObject, "timeout") ?? undefined,
      sessionStartTimeoutMs: numberInput(agent as JsonObject, "sessionStartTimeout") ?? undefined,
      livenessQuietThresholdMs: numberInput(agent as JsonObject, "livenessQuietThresholdMs") ?? undefined,
      probeTimeoutMs: numberInput(agent as JsonObject, "probeTimeoutMs") ?? undefined,
      compaction: resolveCompactionConfigFromInput(agent as JsonObject),
    }
  }
  return {
    model: stringInput(with_, "model") ?? undefined,
    timeoutMs: numberInput(with_, "timeout") ?? undefined,
    sessionStartTimeoutMs: numberInput(with_, "sessionStartTimeout") ?? undefined,
    livenessQuietThresholdMs: numberInput(with_, "livenessQuietThresholdMs") ?? undefined,
    probeTimeoutMs: numberInput(with_, "probeTimeoutMs") ?? undefined,
    compaction: resolveCompactionConfigFromInput(with_),
  }
}

export function buildPromptLoaderContext(context: ActionContext): PromptLoaderContext {
  return {
    with: {},
    variables: context.variables ?? {},
    workDir: context.workDir,
    workId: context.workId,
    title: context.title ?? null,
    stage: context.stage ?? null,
  }
}
