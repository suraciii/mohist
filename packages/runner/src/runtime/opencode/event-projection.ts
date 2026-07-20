import type { RuntimeGlobalEvent } from "./event-subscription.js"
import type { RuntimeTurnEvent } from "./types.js"

interface RuntimeTurnEventProjector {
  project(event: RuntimeGlobalEvent): RuntimeTurnEvent[]
  reconcile(response: unknown): RuntimeTurnEvent[]
}

interface UsageSnapshot {
  inputTokens: number
  outputTokens: number
  totalTokens: number
  cachedReadTokens: number
  thoughtTokens: number
  costAmount: number
}

type TextEventType = "message.delta" | "reasoning.delta"
type TextEventSource = "snapshot" | "delta"

interface ToolCallProjection {
  readonly status: string
  readonly toolName: string
  readonly rawInput: unknown
  readonly fingerprint: string
}

export function createRuntimeTurnEventProjector(
  runtimeSessionId: string,
  workDir: string,
): RuntimeTurnEventProjector {
  const textByPart = new Map<string, string>()
  const textSourceByPart = new Map<string, TextEventSource>()
  const textTypeByPart = new Map<string, TextEventType>()
  const toolByCall = new Map<string, ToolCallProjection>()
  const usageByMessage = new Map<string, UsageSnapshot>()
  const modelByMessage = new Map<string, string>()
  const compactionParts = new Set<string>()

  const build = (type: string, payload: Record<string, unknown>): RuntimeTurnEvent => ({
    type,
    runtimeSessionId,
    workDir,
    payload,
  })

  const appendText = (
    type: TextEventType,
    partId: string,
    messageId: string | null,
    delta: string,
  ): RuntimeTurnEvent[] => {
    if (!delta) return []
    textByPart.set(partId, `${textByPart.get(partId) ?? ""}${delta}`)
    return [build(type, {
      text: delta,
      partId,
      ...(messageId ? { messageId } : {}),
    })]
  }

  const projectTextDelta = (
    type: TextEventType,
    partId: string,
    messageId: string | null,
    delta: string,
  ): RuntimeTurnEvent[] => {
    if (textSourceByPart.get(partId) === "snapshot") return []
    textSourceByPart.set(partId, "delta")
    textTypeByPart.set(partId, type)
    return appendText(type, partId, messageId, delta)
  }

  const reconcileText = (
    type: TextEventType,
    partId: string,
    messageId: string | null,
    text: string,
  ): RuntimeTurnEvent[] => {
    const previous = textByPart.get(partId) ?? ""
    if (text === previous) return []
    if (previous && !text.startsWith(previous)) {
      textByPart.set(partId, text)
      return []
    }
    return appendText(type, partId, messageId, text.slice(previous.length))
  }

  const projectTextSnapshot = (
    type: TextEventType,
    partId: string,
    messageId: string | null,
    text: string,
    final: boolean,
  ): RuntimeTurnEvent[] => {
    textTypeByPart.set(partId, type)
    if (!final && text.length > 0) {
      if (textSourceByPart.get(partId) === "delta") return []
      textSourceByPart.set(partId, "snapshot")
    }
    return reconcileText(type, partId, messageId, text)
  }

  const projectMessage = (info: Record<string, unknown>): RuntimeTurnEvent[] => {
    if (info["role"] !== "assistant") return []
    const messageId = stringValue(info["id"]) ?? "assistant"
    const projected: RuntimeTurnEvent[] = []
    const providerId = stringValue(info["providerID"])
    const modelId = stringValue(info["modelID"])
    if (providerId && modelId) {
      const resolvedModel = `${providerId}/${modelId}`
      if (modelByMessage.get(messageId) !== resolvedModel) {
        modelByMessage.set(messageId, resolvedModel)
        projected.push(build("model.resolved", { resolvedModel, providerId, modelId, messageId }))
      }
    }

    const current = readUsage(info)
    const previous = usageByMessage.get(messageId) ?? emptyUsage()
    usageByMessage.set(messageId, current)
    const delta = subtractUsage(current, previous)
    if (Object.values(delta).some((value) => value > 0)) {
      projected.push(build("usage.updated", {
        ...delta,
        costCurrency: "USD",
        messageId,
      }))
    }
    return projected
  }

  const emitTool = (
    callId: string,
    type: string,
    status: string,
    toolName: string,
    rawInput: unknown,
    payload: Record<string, unknown>,
  ): RuntimeTurnEvent[] => {
    const fingerprint = JSON.stringify([type, payload])
    const previous = toolByCall.get(callId)
    toolByCall.set(callId, { status, toolName, rawInput, fingerprint })
    return previous?.fingerprint === fingerprint ? [] : [build(type, payload)]
  }

  const projectTool = (part: Record<string, unknown>): RuntimeTurnEvent[] => {
    const state = recordValue(part["state"])
    const callId = stringValue(part["callID"])
    const toolName = stringValue(part["tool"])
    const status = stringValue(state?.["status"])
    if (!callId || !toolName || !status) return []
    const previous = toolByCall.get(callId)

    const failed = status === "error"
    const completed = status === "completed" || failed
    const eventType = completed
      ? "tool_call.completed"
      : previous === undefined
        ? "tool_call.started"
        : "tool_call.updated"
    const rawOutput = failed ? state?.["error"] : state?.["output"]
    const rawInput = state?.["input"] ?? previous?.rawInput ?? {}
    const payload = {
      toolCallId: callId,
      toolName,
      status: failed ? "failed" : status,
      state: failed ? "failed" : status,
      rawInput,
      ...(rawOutput !== undefined ? { rawOutput } : {}),
      ...(stringValue(state?.["title"]) ? { title: stringValue(state?.["title"]) } : {}),
    }
    return emitTool(callId, eventType, status, toolName, rawInput, payload)
  }

  const projectPart = (part: Record<string, unknown>, final = false): RuntimeTurnEvent[] => {
    const partId = stringValue(part["id"])
    if (!partId) return []
    const messageId = stringValue(part["messageID"])
    switch (part["type"]) {
      case "text":
        return projectTextSnapshot("message.delta", partId, messageId, stringValue(part["text"]) ?? "", final)
      case "reasoning":
        return projectTextSnapshot("reasoning.delta", partId, messageId, stringValue(part["text"]) ?? "", final)
      case "tool":
        return projectTool(part)
      case "compaction": {
        if (compactionParts.has(partId)) return []
        compactionParts.add(partId)
        return [build("compaction", { partId, messageId, ...part })]
      }
      default:
        return []
    }
  }

  const projectNextTool = (
    payload: Record<string, unknown>,
    sourceState: string,
    status: string,
    type: string,
  ): RuntimeTurnEvent[] => {
    const callId = stringValue(payload["callID"])
    if (!callId) return []
    const previous = toolByCall.get(callId)
    const toolName = stringValue(payload["tool"]) ?? previous?.toolName
    if (!toolName) return []
    const rawInput = payload["input"] ?? previous?.rawInput ?? {}
    const rawOutput = payload["result"] ?? payload["error"] ?? payload["structured"] ?? payload["content"]
    const projected = {
      toolCallId: callId,
      toolName,
      status,
      state: status,
      rawInput,
      ...(rawOutput !== undefined ? { rawOutput } : {}),
    }
    return emitTool(callId, type, sourceState, toolName, rawInput, projected)
  }

  const project = (event: RuntimeGlobalEvent): RuntimeTurnEvent[] => {
    const payload = event.payload ?? {}
    switch (event.type) {
      case "message.updated": {
        const info = recordValue(payload["info"])
        return info ? projectMessage(info) : []
      }
      case "message.part.updated": {
        const part = recordValue(payload["part"])
        return part ? projectPart(part) : []
      }
      case "message.part.delta": {
        const partId = stringValue(payload["partID"])
        const type = partId ? textTypeByPart.get(partId) : undefined
        if (!partId || !type || payload["field"] !== "text") return []
        return projectTextDelta(type, partId, stringValue(payload["messageID"]), stringValue(payload["delta"]) ?? "")
      }
      case "session.next.text.delta":
        return projectTextDelta(
          "message.delta",
          stringValue(payload["textID"]) ?? "text",
          stringValue(payload["assistantMessageID"]),
          stringValue(payload["delta"]) ?? "",
        )
      case "session.next.reasoning.delta":
        return projectTextDelta(
          "reasoning.delta",
          stringValue(payload["reasoningID"]) ?? "reasoning",
          stringValue(payload["assistantMessageID"]),
          stringValue(payload["delta"]) ?? "",
        )
      case "session.next.tool.called":
        return projectNextTool(payload, "running", "running", "tool_call.started")
      case "session.next.tool.progress":
        return projectNextTool(payload, "running", "running", "tool_call.updated")
      case "session.next.tool.success":
        return projectNextTool(payload, "completed", "completed", "tool_call.completed")
      case "session.next.tool.failed":
        return projectNextTool(payload, "error", "failed", "tool_call.completed")
      case "session.next.model.switched": {
        const model = recordValue(payload["model"])
        const providerId = stringValue(model?.["providerID"])
        const modelId = stringValue(model?.["modelID"])
        if (!providerId || !modelId) return []
        return [build("model.resolved", { resolvedModel: `${providerId}/${modelId}`, providerId, modelId })]
      }
      case "session.next.compaction.ended":
      case "session.compacted":
        return [build("compaction", payload)]
      default:
        return []
    }
  }

  const reconcile = (response: unknown): RuntimeTurnEvent[] => {
    const data = recordValue(recordValue(response)?.["data"])
    if (!data) return []
    const projected: RuntimeTurnEvent[] = []
    const info = recordValue(data["info"])
    if (info) projected.push(...projectMessage(info))
    const parts = data["parts"]
    if (Array.isArray(parts)) {
      for (const part of parts) {
        const value = recordValue(part)
        if (value) projected.push(...projectPart(value, true))
      }
    }
    return projected
  }

  return { project, reconcile }
}

function readUsage(info: Record<string, unknown>): UsageSnapshot {
  const tokens = recordValue(info["tokens"])
  const cache = recordValue(tokens?.["cache"])
  const inputTokens = numberValue(tokens?.["input"])
  const outputTokens = numberValue(tokens?.["output"])
  const thoughtTokens = numberValue(tokens?.["reasoning"])
  return {
    inputTokens,
    outputTokens,
    totalTokens: numberValue(tokens?.["total"]) || inputTokens + outputTokens + thoughtTokens,
    cachedReadTokens: numberValue(cache?.["read"]),
    thoughtTokens,
    costAmount: numberValue(info["cost"]),
  }
}

function emptyUsage(): UsageSnapshot {
  return { inputTokens: 0, outputTokens: 0, totalTokens: 0, cachedReadTokens: 0, thoughtTokens: 0, costAmount: 0 }
}

function subtractUsage(current: UsageSnapshot, previous: UsageSnapshot): UsageSnapshot {
  return {
    inputTokens: Math.max(0, current.inputTokens - previous.inputTokens),
    outputTokens: Math.max(0, current.outputTokens - previous.outputTokens),
    totalTokens: Math.max(0, current.totalTokens - previous.totalTokens),
    cachedReadTokens: Math.max(0, current.cachedReadTokens - previous.cachedReadTokens),
    thoughtTokens: Math.max(0, current.thoughtTokens - previous.thoughtTokens),
    costAmount: Math.max(0, current.costAmount - previous.costAmount),
  }
}

function recordValue(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null
}

function stringValue(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null
}

function numberValue(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0
}
