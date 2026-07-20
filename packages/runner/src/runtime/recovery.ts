import type { AddTaskInput, JsonObject, JsonValue, RenderedWorkItem, WorkItemResult } from "../core/types.js"
import { isObject, safeParseObject } from "../core/json.js"
import { getPath } from "../core/json-path.js"

interface RecoveryHandler {
  when?: string
  tasks: AddTaskInput[]
  retrySelf: boolean
}

interface RecoveryConfig {
  budget: number
  handlers: RecoveryHandler[]
}

export class UnresolvedFailureReferenceError extends Error {
  constructor(
    message: string,
    readonly path: string,
    readonly recoveryTaskId: string,
    readonly promptKey?: string,
  ) {
    super(message)
    this.name = "UnresolvedFailureReferenceError"
  }
}

export function tryRecovery(
  work: RenderedWorkItem,
  result: WorkItemResult,
  variables?: JsonObject | null,
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
  const failureContext: JsonObject = {
    output,
    error: result.error ? { code: result.error.code, message: result.error.message } : null,
  }
  const handler = recovery.handlers.find((h) => h.when !== undefined && matchesWhen(h.when!, failureContext))
    ?? (result.error ? recovery.handlers.find((h) => h.when === undefined) : undefined)
  if (!handler) return null

  const effectiveVariables = variables ?? work.variables ?? null
  const renderedHandlerTasks: AddTaskInput[] = []
  for (const task of handler.tasks) {
    try {
      renderedHandlerTasks.push(renderRecoveryTask(task, failureContext, effectiveVariables))
    } catch (error) {
      if (error instanceof UnresolvedFailureReferenceError) {
        return failureResult(work, formatFailureDiagnostic(task.id, error, failureContext))
      }
      throw error
    }
  }

  const addTasks: AddTaskInput[] = [...renderedHandlerTasks]

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
    message: result.error?.message ?? `${label} recovery scheduled`,
    error: result.error,
    output: result.output,
    addTasks,
  }
}

export function matchesWhen(when: string, context: JsonObject): boolean {
  const eq = when.indexOf("=")
  if (eq === -1) return false
  const path = when.slice(0, eq).trim()
  const expected = when.slice(eq + 1).trim()
  return String(getPath(context, path)) === expected
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
    const rawWhen = raw["when"]
    if (rawWhen !== undefined && (typeof rawWhen !== "string" || rawWhen.trim().length === 0)) continue
    const when = typeof rawWhen === "string" ? rawWhen : undefined
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
      // Spec requirement "The canonical declaration survives the complete
      // task lifecycle": recovery handler tasks keep their top-level
      // `expect` alongside `with`. Dropping it here would silently lose
      // the completion contract on the recovery path.
      expect: objectField(entry, "expect"),
      artifacts: objectField(entry, "artifacts"),
      setVars: recordField(entry, "setVars"),
      recovery,
    }
    if (recoveryConfig) task.recoveryRemaining = recoveryConfig.budget
    tasks.push(task)
  }
  return tasks
}

/**
 * Walks a JSON value and substitutes only `${{ failure.* }}` references
 * against the failure context. Every other `${{ }}` namespace passes
 * through byte-for-byte unchanged — those references continue to follow
 * the dispatch-time and execution-time rules in `task-dispatch.md`.
 *
 * Behavior per spec (`specs/recovery-failure-context/spec.md`):
 * - Whole-string `${{ failure.output }}`, `${{ failure.output.X }}`, or
 *   `${{ failure.error.X }}`
 *   preserves the resolved JSON type (object / array / number /
 *   boolean) rather than coercing to a serialized string.
 * - Embedded `... ${{ failure.output.X }} ...` resolves via plain
 *   string substitution.
 * - Unresolvable `${{ failure.* }}` paths throw
 *   {@link UnresolvedFailureReferenceError}; the catch site turns that
 *   into a failed `WorkItemResult`.
 *
 * The `${{ failure.* }}` matcher intentionally duplicates the
 * `REFERENCE_PATTERN` from `core/template.ts` rather than reusing it,
 * because the broader renderer's "embedded unresolved → leave literal"
 * rule is exactly what we must NOT apply here for the failure
 * namespace.
 */
export function expandFailureReferences(value: JsonValue, failureContext: JsonObject): JsonValue {
  const context = "output" in failureContext || "error" in failureContext
    ? failureContext
    : { output: failureContext, error: null }
  return expandFailureValue(value, context)
}

function expandFailureValue(value: JsonValue, failureContext: JsonObject): JsonValue {
  if (typeof value === "string") return expandFailureString(value, failureContext)
  if (Array.isArray(value)) return value.map((item) => expandFailureValue(item, failureContext))
  if (isObject(value)) {
    const result: JsonObject = {}
    for (const [key, child] of Object.entries(value)) {
      result[key] = expandFailureValue(child, failureContext)
    }
    return result
  }
  return value
}

const FAILURE_REFERENCE_PATTERN = /\$\{\{\s*(failure(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}/g
const FAILURE_WHOLE_STRING_PATTERN = /^\s*\$\{\{\s*(failure(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}\s*$/

function expandFailureString(value: string, failureContext: JsonObject): JsonValue {
  const whole = value.match(FAILURE_WHOLE_STRING_PATTERN)
  if (whole) {
    const path = whole[1]
    const resolved = resolveFailurePath(failureContext, path)
    if (resolved === undefined) {
      throw new UnresolvedFailureReferenceError(
        `Recovery task references unresolved failure path '${path}'`,
        path,
        "",
      )
    }
    return resolved
  }
  const next = value.replace(FAILURE_REFERENCE_PATTERN, (match, path: string) => {
    const resolved = resolveFailurePath(failureContext, path)
    if (resolved === undefined) {
      throw new UnresolvedFailureReferenceError(
        `Recovery task references unresolved failure path '${path}'`,
        path,
        "",
      )
    }
    return failureStringify(resolved)
  })
  return next
}

function failureStringify(value: JsonValue): string {
  if (value === null) return ""
  if (typeof value === "string") return value
  if (typeof value === "number" || typeof value === "boolean") return String(value)
  return JSON.stringify(value)
}

function resolveFailurePath(failureContext: JsonObject, path: string): JsonValue | undefined {
  const parts = path.split(".")
  if (parts[0] !== "failure") return undefined
  const remainder = parts.slice(1)
  if (remainder.length === 0) return failureContext
  if (remainder[0] !== "output" && remainder[0] !== "error") return undefined
  let current: JsonValue = failureContext[remainder[0]]
  for (const part of remainder.slice(1)) {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    current = (current as JsonObject)[part]
  }
  return current
}

const PROMPT_REFERENCE_PATTERN = /^\s*\$\{\{\s*prompts\.([A-Za-z_][A-Za-z0-9_-]*)\s*\}\}\s*$/

function renderRecoveryTask(
  task: AddTaskInput,
  failureContext: JsonObject,
  variables: JsonObject | null,
): AddTaskInput {
  const renderedWith = task.with ? renderFieldMap(task.with, failureContext, variables) : task.with ?? null
  const renderedExpect = task.expect ? renderFieldMap(task.expect, failureContext, variables) : task.expect ?? null
  return {
    ...task,
    with: renderedWith,
    expect: renderedExpect,
  }
}

function renderFieldMap(
  input: JsonObject,
  failureContext: JsonObject,
  variables: JsonObject | null,
): JsonObject {
  const result: JsonObject = {}
  for (const [key, value] of Object.entries(input)) {
    result[key] = renderFieldValue(value, failureContext, variables)
  }
  return result
}

function renderFieldValue(value: JsonValue, failureContext: JsonObject, variables: JsonObject | null): JsonValue {
  if (typeof value === "string") {
    return renderFieldString(value, failureContext, variables)
  }
  if (Array.isArray(value)) return value.map((item) => renderFieldValue(item, failureContext, variables))
  if (isObject(value)) return renderFieldMap(value, failureContext, variables)
  return value
}

function renderFieldString(
  value: string,
  failureContext: JsonObject,
  variables: JsonObject | null,
): JsonValue {
  const promptMatch = value.match(PROMPT_REFERENCE_PATTERN)
  if (promptMatch) {
    const key = promptMatch[1]
    const body = resolvePromptBody(variables, key)
    if (body === undefined) {
      throw new Error("Recovery task references ${{ prompts." + key + " }} but the prompt body is not available in the dispatch context")
    }
    try {
      return expandFailureValue(body, failureContext)
    } catch (error) {
      if (error instanceof UnresolvedFailureReferenceError) {
        throw new UnresolvedFailureReferenceError(error.message, error.path, error.recoveryTaskId, key)
      }
      throw error
    }
  }
  return expandFailureString(value, failureContext)
}

function resolvePromptBody(variables: JsonObject | null, key: string): JsonValue | undefined {
  if (!variables) return undefined
  const prompts = variables["prompts"]
  if (!isObject(prompts)) return undefined
  const body = prompts[key]
  return body ?? undefined
}

function formatFailureDiagnostic(
  recoveryTaskId: string,
  error: UnresolvedFailureReferenceError,
  failureContext: JsonObject,
): string {
  const prompt = error.promptKey ? ` in Prompt '${error.promptKey}'` : ""
  return `recovery task '${recoveryTaskId}'${prompt} references unresolved failure expression '${"${{ " + error.path + " }}"}'. ` +
    `Available failure context: ${availableFailureContext(failureContext)}.`
}

function availableFailureContext(failureContext: JsonObject): string {
  const output = describeFailureValue(failureContext.output)
  const error = describeFailureValue(failureContext.error)
  return `failure.output ${output}; failure.error ${error}`
}

function describeFailureValue(value: JsonValue | undefined): string {
  if (value === null || value === undefined) return "is unavailable"
  if (!isObject(value)) return `is ${typeof value}`
  const fields = Object.keys(value).sort()
  return fields.length === 0 ? "has no fields" : `fields [${fields.join(", ")}]`
}

function failureResult(work: RenderedWorkItem, message: string): WorkItemResult {
  const label = work.title?.trim() || work.uses || work.workId
  const failureMessage = `${label}: ${message}`
  return {
    status: "failed",
    message: failureMessage,
    error: { code: "recovery-reference-unresolved", message: failureMessage },
  }
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
