import type { JsonObject } from "../../core/types.js"
import { numberInput, objectInput, stringInput } from "../../core/json.js"

export type CompactionStrategy = "summary"

export interface CompactionConfig {
  threshold: number
  strategy: CompactionStrategy
}

export interface CompactionEventPayload {
  contextWindowUsedBefore?: number
  contextWindowUsedAfter?: number
  contextWindowSize?: number
  strategy?: CompactionStrategy
}

const DEFAULT_COMPACTION_THRESHOLD = 0.8
const DEFAULT_COMPACTION_STRATEGY = "summary"
const COMPACTION_META_KEY = "opencode.compaction"

export function resolveCompactionConfigFromInput(input: JsonObject | null | undefined): CompactionConfig | undefined {
  if (!input || typeof input !== "object") return undefined
  const raw = objectInput(input, "compaction")
  if (!raw || typeof raw !== "object") return undefined
  const thresholdValue = numberInput(raw as JsonObject, "threshold")
  const strategyValue = stringInput(raw as JsonObject, "strategy")
  if (thresholdValue === undefined && strategyValue === undefined) return undefined
  return {
    threshold: thresholdValue !== undefined && Number.isFinite(thresholdValue) && thresholdValue >= 0 && thresholdValue <= 1
      ? thresholdValue
      : DEFAULT_COMPACTION_THRESHOLD,
    strategy: strategyValue === "summary" ? "summary" : DEFAULT_COMPACTION_STRATEGY,
  }
}

export function resolveCompactionConfig(agentConfig?: { compaction?: CompactionConfig }): CompactionConfig {
  if (!agentConfig?.compaction) return defaultCompactionConfig()
  return {
    threshold: agentConfig.compaction.threshold,
    strategy: agentConfig.compaction.strategy,
  }
}

export function defaultCompactionConfig(): CompactionConfig {
  return {
    threshold: DEFAULT_COMPACTION_THRESHOLD,
    strategy: DEFAULT_COMPACTION_STRATEGY,
  }
}

export function buildSessionMeta(compaction: CompactionConfig): { [key: string]: unknown } {
  return {
    [COMPACTION_META_KEY]: {
      threshold: compaction.threshold,
      strategy: compaction.strategy,
    },
  }
}

function numberField(record: Record<string, unknown>, key: string): number | undefined {
  const value = record[key]
  return typeof value === "number" && Number.isFinite(value) ? value : undefined
}

export function extractCompactionEventFromUpdate(update: unknown): CompactionEventPayload | undefined {
  if (!update || typeof update !== "object") return undefined
  const record = update as Record<string, unknown>
  const candidates: Array<Record<string, unknown>> = []
  if (record.compaction && typeof record.compaction === "object") {
    candidates.push(record.compaction as Record<string, unknown>)
  }
  const meta = record._meta
  if (meta && typeof meta === "object") {
    const metaRecord = meta as Record<string, unknown>
    if (metaRecord.compaction && typeof metaRecord.compaction === "object") {
      candidates.push(metaRecord.compaction as Record<string, unknown>)
    }
    if (metaRecord["opencode.compaction"] && typeof metaRecord["opencode.compaction"] === "object") {
      candidates.push(metaRecord["opencode.compaction"] as Record<string, unknown>)
    }
  }
  let before: number | undefined
  let after: number | undefined
  let size: number | undefined
  let strategyValue: unknown
  for (const source of candidates) {
    before ??= numberField(source, "contextWindowUsedBefore")
    after ??= numberField(source, "contextWindowUsedAfter")
    size ??= numberField(source, "contextWindowSize")
    if (strategyValue === undefined) strategyValue = source.strategy
  }
  const strategy: CompactionStrategy | undefined = strategyValue === "summary" ? "summary" : undefined
  if (before === undefined && after === undefined && size === undefined && strategy === undefined) {
    return undefined
  }
  return {
    contextWindowUsedBefore: before,
    contextWindowUsedAfter: after,
    contextWindowSize: size,
    strategy,
  }
}
