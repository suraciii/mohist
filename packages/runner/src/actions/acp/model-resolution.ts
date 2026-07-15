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
  variant?: string
  source: "agent.model" | "with.model" | "none"
}

export function resolveRequestedModel(context: ActionContext, agentConfig?: { model?: string; variant?: string }): RequestedModel {
  const agentModel = agentConfig?.model
  if (agentModel?.trim()) return composeRequestedModel(agentModel, agentConfig?.variant, "agent.model")
  const withModel = stringInput(context.with, "model")
  if (withModel?.trim()) return composeRequestedModel(withModel, stringInput(context.with, "variant"), "with.model")
  return { source: "none" }
}

function composeRequestedModel(model: string, variant: string | undefined, source: "agent.model" | "with.model"): RequestedModel {
  const trimmedModel = model.trim()
  const trimmedVariant = variant?.trim()
  if (!trimmedVariant) return { model: trimmedModel, source }
  return { model: `${trimmedModel}/${trimmedVariant}`, variant, source }
}

export async function applyRequestedModel(
  connection: ClientSideConnection,
  context: ActionContext,
  sessionId: string,
  requested: RequestedModel,
  notify: (activityType?: string) => void,
) {
  if (!requested.model?.trim()) {
    console.warn("mohist acp model not configured; using provider default", modelDiagnosticContext(context, requested, null))
    return
  }

  console.info("mohist acp setting requested model", modelDiagnosticContext(context, requested, null))
  let variantDelivered = false
  try {
    await connection.unstable_setSessionModel({ sessionId, modelId: requested.model })
    variantDelivered = true
    recordLivenessActivity(notify, classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_model" }))
    console.info("mohist acp set model via set_session_model", modelDiagnosticContext(context, requested, variantDelivered))
  } catch (modelError) {
    console.warn("mohist acp set requested model failed; provider default may be used", { ...modelDiagnosticContext(context, requested, variantDelivered), error: errorMessage(modelError) })
  }
}

export function modelDiagnosticContext(context: ActionContext, requested: RequestedModel, variantDelivered: boolean | null) {
  return {
    workflowRunId: context.workflowRunId,
    workId: context.workId,
    stage: context.stage,
    sessionName: sessionNameFromContext(context),
    requestedModel: requested.model ?? null,
    requestedModelSource: requested.source,
    ...(requested.variant !== undefined ? { requestedVariant: requested.variant } : {}),
    ...(variantDelivered === null ? {} : { variantDelivered }),
  }
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
