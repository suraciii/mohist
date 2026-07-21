import { CredentialMasker } from "../task-log.js"
import type { PiRuntimeEvent } from "./types.js"

export interface PiProjector {
  project(source: unknown): PiRuntimeEvent[]
  reconcile(messages: readonly { readonly role?: string; readonly content?: unknown; readonly usage?: Record<string, unknown> }[]): PiRuntimeEvent[]
  diagnostics(): readonly { readonly code: string; readonly message: string }[]
}

export function createPiProjector(runtimeSessionId: string, workDir: string, masker = new CredentialMasker()): PiProjector {
  const seen = new Set<string>()
  const unknown: { code: string; message: string }[] = []
  let sequence = 0
  const emit = (type: string, key: string, payload: Record<string, unknown>): PiRuntimeEvent[] => {
    if (seen.has(key)) return []
    seen.add(key)
    return [{ id: key || `pi-${++sequence}`, type, runtimeSessionId, workDir, payload: maskPayload(payload, masker) }]
  }
  return {
    project(source) {
      if (!source || typeof source !== "object" || typeof (source as { type?: unknown }).type !== "string") return []
      const event = source as Record<string, unknown> & { type: string }
      const id = stringValue(event.id) ?? stringValue(event.messageId) ?? stringValue(event.toolCallId) ?? `${event.type}:${++sequence}`
      switch (event.type) {
        case "message_start": case "message_update": case "message_end": {
          const facts = emit("message", id, { role: event.role, content: event.content, delta: event.delta })
          const usage = recordValue(event.usage)
          if (usage) facts.push(...emit("usage.updated", `${id}:usage`, normalizeUsage(usage)))
          return facts
        }
        case "tool_execution_start": case "tool_execution_update": case "tool_execution_end": return emit("tool", stringValue(event.toolCallId) ?? id, { phase: event.type.slice("tool_execution_".length), toolCallId: event.toolCallId, toolName: event.toolName, input: event.input, output: event.output, error: event.error })
        case "compaction_start": return emit("compaction_event", id, { phase: "started" })
        case "compaction_end": return emit("compaction_event", id, { phase: "completed", error: event.errorMessage })
        case "auto_retry_start": return emit("provider.retry", id, { phase: "started", attempt: event.attempt, maxAttempts: event.maxAttempts, delayMs: event.delayMs, message: stringValue(event.errorMessage) })
        case "auto_retry_end": return emit("provider.retry", id, { phase: "ended", attempt: event.attempt, success: event.success, message: event.finalError })
        case "model_change": case "thinking_level_changed": case "turn_start": case "turn_end": case "agent_end": return emit("status", id, { source: event.type, model: event.model, variant: event.level ?? event.thinkingLevel, stopReason: event.stopReason })
        case "message": {
          const facts = emit(event.role === "assistant" ? "assistant.text" : "message", id, { role: event.role, content: event.content })
          const usage = recordValue(event.usage)
          if (usage) facts.push(...emit("usage.updated", `${id}:usage`, normalizeUsage(usage)))
          return facts
        }
        default:
          unknown.push({ code: "unknown-pi-event", message: `Ignored unknown Pi event: ${event.type}` })
          return []
      }
    },
    reconcile(messages) {
      const facts: PiRuntimeEvent[] = []
      messages.forEach((message, index) => {
        if (message.role === "assistant") facts.push(...emit("message", `final-assistant-${index}`, { role: "assistant", content: message.content, usage: message.usage }))
        if (message.role === "toolResult") facts.push(...emit("tool", `final-tool-${index}`, { phase: "completed", content: message.content }))
      })
      return facts
    },
    diagnostics: () => unknown,
  }
}

function stringValue(value: unknown): string | undefined { return typeof value === "string" ? value : undefined }
function recordValue(value: unknown): Record<string, unknown> | undefined { return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined }
function numberValue(value: unknown): number | undefined { return typeof value === "number" && Number.isFinite(value) ? value : undefined }
function normalizeUsage(usage: Record<string, unknown>): Record<string, unknown> {
  const cost = recordValue(usage.cost)
  return {
    ...(numberValue(usage.input) !== undefined ? { inputTokens: numberValue(usage.input) } : {}),
    ...(numberValue(usage.output) !== undefined ? { outputTokens: numberValue(usage.output) } : {}),
    ...(numberValue(usage.cacheRead) !== undefined ? { cacheReadTokens: numberValue(usage.cacheRead) } : {}),
    ...(numberValue(usage.cacheWrite) !== undefined ? { cacheWriteTokens: numberValue(usage.cacheWrite) } : {}),
    ...(numberValue(usage.thought) !== undefined ? { thoughtTokens: numberValue(usage.thought) } : {}),
    ...(numberValue(cost?.amount) !== undefined ? { costAmount: numberValue(cost?.amount) } : {}),
    ...(typeof cost?.currency === "string" ? { costCurrency: cost.currency } : {}),
  }
}

function maskPayload(payload: Record<string, unknown>, masker: CredentialMasker): Record<string, unknown> {
  const result: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(payload)) result[key] = typeof value === "string" ? masker.mask(value) : value
  return result
}
