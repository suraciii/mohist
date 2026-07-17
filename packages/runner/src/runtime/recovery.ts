import type { AddTaskInput, JsonObject, RenderedWorkItem, WorkItemResult } from "../core/types.js"
import { isObject, safeParseObject } from "../core/json.js"

interface RecoveryHandler {
  when: string
  tasks: AddTaskInput[]
  retrySelf: boolean
}

interface RecoveryConfig {
  budget: number
  handlers: RecoveryHandler[]
}

export function tryRecovery(
  work: RenderedWorkItem,
  result: WorkItemResult,
): WorkItemResult | null {
  const recovery = readRecoveryConfig(work.recovery)
  if (!recovery) return null

  if (!Object.prototype.hasOwnProperty.call(work, "recoveryRemaining")) return null
  const rawRemaining = work.recoveryRemaining
  if (rawRemaining !== null && (typeof rawRemaining !== "number" || !Number.isFinite(rawRemaining))) return null
  const remaining = rawRemaining === null
    ? recovery.budget
    : clampRemaining(rawRemaining, recovery.budget)
  if (remaining <= 0) return null

  const output = safeParseObject(result.output)
  if (!output) return null

  const handler = recovery.handlers.find((h) => matchesWhen(h.when, output))
  if (!handler) return null

  const addTasks: AddTaskInput[] = [...handler.tasks]

  if (handler.retrySelf) {
    const retryId = work.workId.includes(".")
      ? work.workId.substring(0, work.workId.lastIndexOf("."))
      : work.workId
    addTasks.push({
      id: retryId,
      title: work.title ?? work.workId,
      uses: work.uses ?? null,
      with: work.with,
      artifacts: work.artifacts,
      setVars: work.setVars ?? null,
      recovery: work.recovery,
      recoveryRemaining: remaining - 1,
      expect: work.expect ?? null,
    })
  }

  const label = work.title?.trim() || work.uses || work.workId
  return {
    status: "completed",
    message: `${label} failed (${handler.when}); recovery scheduled`,
    output: result.output,
    addTasks,
  }
}

export function matchesWhen(when: string, output: JsonObject): boolean {
  const eq = when.indexOf("=")
  if (eq === -1) return false
  const field = when.slice(0, eq).trim()
  const expected = when.slice(eq + 1).trim()
  return String(output[field]) === expected
}

export function readRecoveryConfig(recovery: JsonObject | null | undefined): RecoveryConfig | null {
  if (!recovery) return null
  const rawBudget = recovery["budget"]
  const budget = typeof rawBudget === "number" && Number.isFinite(rawBudget) ? Math.max(0, Math.floor(rawBudget)) : 0
  const rawHandlers = recovery["handlers"]
  if (!Array.isArray(rawHandlers)) return null
  const handlers: RecoveryHandler[] = []
  for (const raw of rawHandlers) {
    if (!isObject(raw)) continue
    const when = stringField(raw, "when")
    if (!when) continue
    handlers.push({
      when,
      tasks: readAddTasks(raw["tasks"]),
      retrySelf: raw["retrySelf"] === true,
    })
  }
  return { budget, handlers }
}

export function readAddTasks(raw: unknown): AddTaskInput[] {
  if (!Array.isArray(raw)) return []
  const tasks: AddTaskInput[] = []
  for (const entry of raw) {
    if (!isObject(entry)) continue
    const id = stringField(entry, "id")
    if (!id) continue
    const recovery = objectField(entry, "recovery")
    const recoveryConfig = readRecoveryConfig(recovery)
    const task: AddTaskInput = {
      id,
      title: stringField(entry, "title") ?? id,
      uses: stringField(entry, "uses"),
      with: objectField(entry, "with"),
      artifacts: objectField(entry, "artifacts"),
      setVars: recordField(entry, "setVars"),
      recovery,
    }
    if (recoveryConfig) task.recoveryRemaining = recoveryConfig.budget
    tasks.push(task)
  }
  return tasks
}

function clampRemaining(value: number, budget: number): number {
  return Math.min(budget, Math.max(0, Math.floor(value)))
}

function stringField(obj: JsonObject, key: string): string | null {
  const value = obj[key]
  return typeof value === "string" ? value : null
}

function objectField(obj: JsonObject, key: string): JsonObject | null {
  const value = obj[key]
  return isObject(value) ? value : null
}

function recordField(obj: JsonObject, key: string): Record<string, string> | null {
  const value = obj[key]
  if (!isObject(value)) return null
  const result: Record<string, string> = {}
  for (const [entryKey, entryValue] of Object.entries(value)) {
    if (typeof entryValue === "string") result[entryKey] = entryValue
  }
  return Object.keys(result).length > 0 ? result : null
}
