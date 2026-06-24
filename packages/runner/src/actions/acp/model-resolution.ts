import type { ClientSideConnection } from "@agentclientprotocol/sdk"
import type { ActionContext, JsonObject } from "../../core/types.js"
import { stringInput } from "../../core/json.js"
import {
  classifyAcpLivenessActivity,
  emitResolvedModelEvent,
  recordLivenessActivity,
  sessionNameFromContext,
} from "./session-events.js"

export interface RequestedModel {
  model?: string
  source: "agent.model" | "with.model" | "none"
}

export function resolveRequestedModel(context: ActionContext, agentConfig?: { model?: string }): RequestedModel {
  const agentModel = agentConfig?.model
  if (agentModel?.trim()) return { model: agentModel, source: "agent.model" }
  const withModel = stringInput(context.with, "model")
  if (withModel?.trim()) return { model: withModel, source: "with.model" }
  return { source: "none" }
}

export async function applyRequestedModel(
  connection: ClientSideConnection,
  context: ActionContext,
  sessionId: string,
  requested: RequestedModel,
  notify: (activityType?: string) => void,
) {
  if (!requested.model?.trim()) {
    console.warn("mohist acp model not configured; using provider default", modelDiagnosticContext(context, requested))
    return
  }

  console.info("mohist acp setting requested model", modelDiagnosticContext(context, requested))
  try {
    await connection.setSessionConfigOption({ sessionId, configId: "model", value: requested.model })
    recordLivenessActivity(notify, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_config" }))
    console.info("mohist acp set model via config option", modelDiagnosticContext(context, requested))
  } catch (configError) {
    console.warn("mohist acp set model via config option failed; trying set_session_model", { ...modelDiagnosticContext(context, requested), error: errorMessage(configError) })
    try {
      await connection.unstable_setSessionModel({ sessionId, modelId: requested.model })
      recordLivenessActivity(notify, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_model" }))
      console.info("mohist acp set model via set_session_model", modelDiagnosticContext(context, requested))
    } catch (modelError) {
      console.warn("mohist acp set requested model failed; provider default may be used", { ...modelDiagnosticContext(context, requested), error: errorMessage(modelError) })
    }
  }
}

export function modelDiagnosticContext(context: ActionContext, requested: RequestedModel) {
  return {
    workflowRunId: context.workflowRunId,
    workId: context.workId,
    stage: context.stage,
    sessionName: sessionNameFromContext(context),
    requestedModel: requested.model ?? null,
    requestedModelSource: requested.source,
  }
}

export function requestedModelMatchesSession(requestedModel: string | undefined, sessionModel: string | null | undefined) {
  const requested = requestedModel?.trim()
  if (!requested) return true
  return sessionModel?.trim() === requested
}

export function cachedModelAllowsReuse(requestedModel: string | undefined, cachedModel: string | null | undefined) {
  const requested = requestedModel?.trim()
  if (!requested) return true
  const cached = cachedModel?.trim()
  if (!cached) return true
  return cached === requested
}

export function extractResolvedModelId(value: unknown): string | undefined {
  if (typeof value !== "object" || value === null) return undefined
  const models = (value as Record<string, unknown>).models
  if (typeof models !== "object" || models === null) return undefined
  const current = (models as Record<string, unknown>).currentModelId
  return typeof current === "string" && current.trim().length > 0 ? current : undefined
}

export function extractResolvedModelFromConfigUpdate(value: unknown): string | undefined {
  if (typeof value !== "object" || value === null) return undefined
  const configOptions = (value as Record<string, unknown>).configOptions
  if (!Array.isArray(configOptions)) return undefined
  for (const entry of configOptions) {
    if (typeof entry !== "object" || entry === null) continue
    const option = entry as Record<string, unknown>
    const category = option.category
    if (category !== "model") continue
    const current = option.currentValue
    if (typeof current === "string" && current.trim().length > 0) return current
  }
  return undefined
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : String(error)
}
