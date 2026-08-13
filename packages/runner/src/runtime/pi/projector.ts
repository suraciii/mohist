import { CredentialMasker } from "../task-log.js"
import type { PiRuntimeEvent } from "./types.js"

export interface PiProjector {
  project(source: unknown): PiRuntimeEvent[]
  reconcile(messages: readonly { readonly role?: string; readonly content?: unknown; readonly usage?: Record<string, unknown>; readonly toolCallId?: string; readonly toolName?: string; readonly isError?: boolean }[]): PiRuntimeEvent[]
  diagnostics(): readonly { readonly code: string; readonly message: string }[]
}

type TextEventType = "message.delta" | "reasoning.delta"

interface ToolProjection {
  readonly toolName: string
  readonly rawInput: unknown
  readonly rawOutput?: unknown
  readonly status: string
}

export function createPiProjector(runtimeSessionId: string, workDir: string, masker = new CredentialMasker()): PiProjector {
  const seen = new Set<string>()
  const textByPart = new Map<string, string>()
  const toolByCall = new Map<string, ToolProjection>()
  const messageIds = new WeakMap<object, string>()
  const unknown: { code: string; message: string }[] = []
  let sequence = 0
  let assistantSequence = 0
  let activeAssistantId: string | null = null

  const emitOnce = (type: string, key: string, payload: Record<string, unknown>): PiRuntimeEvent[] => {
    if (seen.has(key)) return []
    seen.add(key)
    return [build(type, key, payload)]
  }

  const emit = (type: string, payload: Record<string, unknown>): PiRuntimeEvent =>
    build(type, `pi-${++sequence}`, payload)

  const projectTextDelta = (type: TextEventType, partId: string, messageId: string, delta: string): PiRuntimeEvent[] => {
    if (!delta) return []
    textByPart.set(partId, `${textByPart.get(partId) ?? ""}${delta}`)
    return [emit(type, { text: delta, partId, messageId })]
  }

  const reconcileText = (type: TextEventType, partId: string, messageId: string, text: string): PiRuntimeEvent[] => {
    if (!text) return []
    const previous = textByPart.get(partId) ?? ""
    if (text === previous) return []
    if (previous && !text.startsWith(previous)) {
      textByPart.set(partId, text)
      return []
    }
    return projectTextDelta(type, partId, messageId, text.slice(previous.length))
  }

  const projectAssistantMessage = (message: Record<string, unknown>, messageId: string): PiRuntimeEvent[] => {
    const facts: PiRuntimeEvent[] = []
    const content = Array.isArray(message.content) ? message.content : []
    content.forEach((part, index) => {
      const value = recordValue(part)
      if (!value) return
      const partType = stringValue(value.type)
      if (partType === "text") facts.push(...reconcileText("message.delta", `${messageId}:message.delta:${index}`, messageId, stringValue(value.text) ?? ""))
      if (partType === "thinking") facts.push(...reconcileText("reasoning.delta", `${messageId}:reasoning.delta:${index}`, messageId, stringValue(value.thinking) ?? ""))
    })
    const usage = recordValue(message.usage)
    if (usage) facts.push(...emitOnce("usage.updated", `${messageId}:usage`, normalizeUsage(usage)))
    return facts
  }

  const messageIdFor = (message: unknown, role: string | undefined): string => {
    const value = recordValue(message)
    const explicit = stringValue(value?.id) ?? stringValue(value?.messageId)
    if (explicit) return explicit
    if (value && role === "assistant") {
      const existing = messageIds.get(value)
      if (existing) return existing
      const id = activeAssistantId ?? `assistant-${++assistantSequence}`
      messageIds.set(value, id)
      return id
    }
    return activeAssistantId ?? `message-${++sequence}`
  }

  const projectTool = (event: Record<string, unknown>, phase: "started" | "updated" | "completed"): PiRuntimeEvent[] => {
    const toolCallId = stringValue(event.toolCallId)
    const toolName = stringValue(event.toolName)
    if (!toolCallId || !toolName) return []
    const previous = toolByCall.get(toolCallId)
    const rawInput = event.args ?? previous?.rawInput ?? {}
    const isError = phase === "completed" && event.isError === true
    const rawOutput = event.result ?? event.partialResult ?? previous?.rawOutput
    const status = phase === "completed" ? (isError ? "failed" : "completed") : "running"
    const payload: Record<string, unknown> = {
      toolCallId,
      toolName,
      status,
      state: status,
      rawInput,
      ...(rawOutput !== undefined ? { rawOutput } : {}),
      ...(isError ? { error: stringValue(event.error) ?? "Tool failed" } : {}),
    }
    const fingerprint = JSON.stringify([phase, payload])
    if (previous && JSON.stringify([phase, {
      toolCallId,
      toolName: previous.toolName,
      status: previous.status,
      state: previous.status,
      rawInput: previous.rawInput,
      ...(previous.rawOutput !== undefined ? { rawOutput: previous.rawOutput } : {}),
    }]) === fingerprint) return []
    toolByCall.set(toolCallId, { toolName, rawInput, ...(rawOutput !== undefined ? { rawOutput } : {}), status })
    return [emit(phase === "started" ? "tool_call.started" : phase === "updated" ? "tool_call.updated" : "tool_call.completed", payload)]
  }

  return {
    project(source) {
      const event = recordValue(source)
      const eventType = stringValue(event?.type)
      if (!event || !eventType) return []

      switch (eventType) {
        case "message_start": {
          const message = recordValue(event.message)
          if (message?.role === "assistant") {
            activeAssistantId = `assistant-${++assistantSequence}`
            messageIds.set(message, activeAssistantId)
          }
          return []
        }
        case "message_update": {
          const message = recordValue(event.message)
          const assistantEvent = recordValue(event.assistantMessageEvent)
          if (message?.role !== "assistant" || !assistantEvent) return []
          const messageId = messageIdFor(message, "assistant")
          const contentIndex = typeof assistantEvent.contentIndex === "number" ? assistantEvent.contentIndex : 0
          const eventKind = stringValue(assistantEvent.type)
          const textType = eventKind?.startsWith("thinking") ? "reasoning.delta" : "message.delta"
          const partId = `${messageId}:${textType}:${contentIndex}`
          if (eventKind === "text_delta" || eventKind === "thinking_delta") {
            return projectTextDelta(textType, partId, messageId, stringValue(assistantEvent.delta) ?? "")
          }
          if (eventKind === "text_end") {
            return reconcileText("message.delta", partId, messageId, stringValue(assistantEvent.content) ?? "")
          }
          if (eventKind === "thinking_end") {
            return reconcileText("reasoning.delta", partId, messageId, stringValue(assistantEvent.content) ?? "")
          }
          return []
        }
        case "message_end": {
          const message = recordValue(event.message)
          if (message?.role !== "assistant") return []
          return projectAssistantMessage(message, messageIdFor(message, "assistant"))
        }
        case "tool_execution_start": return projectTool(event, "started")
        case "tool_execution_update": return projectTool(event, "updated")
        case "tool_execution_end": return projectTool(event, "completed")
        case "compaction_start": return emitOnce("compaction", eventId(event, "compaction-start"), { phase: "started" })
        case "compaction_end": return emitOnce("compaction", eventId(event, "compaction-end"), { phase: "completed", error: event.errorMessage })
        case "auto_retry_start": return emitOnce("provider.retry", eventId(event, "retry-start"), { phase: "started", attempt: event.attempt, maxAttempts: event.maxAttempts, delayMs: event.delayMs, message: stringValue(event.errorMessage) })
        case "auto_retry_end": return emitOnce("provider.retry", eventId(event, "retry-end"), { phase: "ended", attempt: event.attempt, success: event.success, message: event.finalError })
        case "model_change": return emitResolvedModel(eventId(event, "model"), event, emitOnce)
        case "thinking_level_changed": case "thinking_level_select": case "turn_start": case "turn_end": case "agent_start": case "agent_end": case "agent_settled":
          return []
        default:
          unknown.push({ code: "unknown-pi-event", message: `Ignored unknown Pi event: ${eventType}` })
          return []
      }
    },
    reconcile(messages) {
      const facts: PiRuntimeEvent[] = []
      messages.forEach((message, index) => {
        const value = recordValue(message)
        if (!value) return
        if (message.role === "assistant") facts.push(...projectAssistantMessage(value, activeAssistantId ?? `assistant-${index}`))
        if (message.role === "toolResult") {
          const toolCallId = stringValue(message.toolCallId)
          const toolName = stringValue(message.toolName) ?? "unknown"
          if (!toolCallId || toolByCall.has(toolCallId)) return
          const rawOutput = message.content
          toolByCall.set(toolCallId, { toolName, rawInput: {}, rawOutput, status: message.isError ? "failed" : "completed" })
          facts.push(emit("tool_call.completed", {
            toolCallId,
            toolName,
            status: message.isError ? "failed" : "completed",
            state: message.isError ? "failed" : "completed",
            rawInput: {},
            rawOutput,
            ...(message.isError ? { error: "Tool failed" } : {}),
          }))
        }
      })
      return facts
    },
    diagnostics: () => unknown,
  }

  function build(type: string, key: string, payload: Record<string, unknown>): PiRuntimeEvent {
    return { id: key, type, runtimeSessionId, workDir, payload: maskPayload(payload, masker) }
  }
}

function eventId(event: Record<string, unknown>, fallback: string): string {
  return stringValue(event.id) ?? stringValue(event.messageId) ?? `${fallback}:${JSON.stringify(event)}`
}

function stringValue(value: unknown): string | undefined { return typeof value === "string" ? value : undefined }
function recordValue(value: unknown): Record<string, unknown> | undefined { return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined }

function emitResolvedModel(
  id: string,
  event: Record<string, unknown>,
  emit: (type: string, key: string, payload: Record<string, unknown>) => PiRuntimeEvent[],
): PiRuntimeEvent[] {
  const model = event.model
  const fromObject = recordValue(model)
  let provider = stringValue(event.provider) ?? stringValue(fromObject?.["provider"])
  let modelId = stringValue(event.modelId) ?? stringValue(fromObject?.["id"]) ?? stringValue(fromObject?.["modelId"])
  let resolvedModel: string | undefined
  if (provider && modelId) resolvedModel = `${provider}/${modelId}`
  else if (typeof model === "string" && model.length > 0) {
    const split = splitProviderModel(model)
    if (split) {
      provider ??= split.provider
      modelId ??= split.id
      resolvedModel = `${split.provider}/${split.id}`
    } else {
      resolvedModel = model
    }
  }
  if (!resolvedModel) return []
  const payload: Record<string, unknown> = { resolvedModel }
  if (provider) payload["providerId"] = provider
  if (modelId) payload["modelId"] = modelId
  return emit("model.resolved", id, payload)
}

function splitProviderModel(value: string): { provider: string; id: string } | null {
  const index = value.indexOf("/")
  return index > 0 && index < value.length - 1 ? { provider: value.slice(0, index), id: value.slice(index + 1) } : null
}
function numberValue(value: unknown): number | undefined { return typeof value === "number" && Number.isFinite(value) ? value : undefined }
function normalizeUsage(usage: Record<string, unknown>): Record<string, unknown> {
  const cost = recordValue(usage.cost)
  return {
    ...(numberValue(usage.input) !== undefined ? { inputTokens: numberValue(usage.input) } : {}),
    ...(numberValue(usage.output) !== undefined ? { outputTokens: numberValue(usage.output) } : {}),
    ...(numberValue(usage.cacheRead) !== undefined ? { cacheReadTokens: numberValue(usage.cacheRead) } : {}),
    ...(numberValue(usage.cacheWrite) !== undefined ? { cacheWriteTokens: numberValue(usage.cacheWrite) } : {}),
    ...(numberValue(usage.reasoning) !== undefined ? { thoughtTokens: numberValue(usage.reasoning) } : {}),
    ...(numberValue(usage.thought) !== undefined ? { thoughtTokens: numberValue(usage.thought) } : {}),
    ...(numberValue(cost?.total) !== undefined ? { costAmount: numberValue(cost?.total) } : numberValue(cost?.amount) !== undefined ? { costAmount: numberValue(cost?.amount) } : {}),
    ...(typeof cost?.currency === "string" ? { costCurrency: cost.currency } : {}),
  }
}

function maskPayload(payload: Record<string, unknown>, masker: CredentialMasker): Record<string, unknown> {
  const result: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(payload)) result[key] = typeof value === "string" ? masker.mask(value) : value
  return result
}
