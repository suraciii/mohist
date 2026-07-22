import type { ActionError, JsonObject, JsonValue, WorkItemResult } from "../core/types.js"
import { renderWithSkippedFields, unresolvedReferences } from "../core/template.js"
import type { ActionRegistry } from "../actions/registry.js"
import { validateActionInput, deferredInputFields, injectEngineInputs } from "../actions/input-validation.js"
import { malformedToUnexpectedError, normalizeActionResult } from "../actions/result-validation.js"
import { errorMessage } from "../core/errors.js"
import type { ActionHost } from "../actions/host.js"
import { capabilitySet } from "../actions/host.js"
import type { ActionCapabilitySet } from "../actions/manifest.js"

function rowToJsonValue(row: CheckResultRow & { invalidOutputReason?: string }): JsonValue {
  return { ...row } as unknown as JsonValue
}

export interface CheckDeclaration {
  name?: string
  title?: string
  uses: string
  with?: JsonObject | null
}

export interface CheckResultRow {
  name?: string
  status: string
  message?: string | null
  output?: JsonObject | null
  error?: ActionError | null
}

export interface CheckExecutionDeps {
  actions: ActionRegistry
  buildHost: (work: any, signal: AbortSignal, workDir: string, caps: ActionCapabilitySet) => ActionHost
  formatUnresolved: (unresolved: string[]) => string
  resolveWorkDir: (withInput: JsonObject | null) => Promise<string>
  toCheckStatus: (status: string) => string
}

export async function executeCheckDispatch(
  checks: CheckDeclaration[],
  variables: JsonObject,
  deps: CheckExecutionDeps,
): Promise<WorkItemResult> {
  if (checks.length === 0) {
    const message = "No checks found in dispatch"
    return { status: "fail", message, error: { code: "invalid-check-dispatch", message } }
  }

  const results = await Promise.all(checks.map((check) => runOneCheck(check, variables, deps)))
  for (const result of results) {
    const invalidReason = result.invalidOutputReason
    if (invalidReason) {
      const message = `Check '${result.name ?? "(unnamed)"}' produced invalid output: ${invalidReason}`
      const publicResults = results.map(({ invalidOutputReason: _ignored, ...row }) => row)
      return { status: "fail", message, error: { code: "unexpected-error", message }, output: publicResults.map(rowToJsonValue) }
    }
  }
  const cleaned = results.map(({ invalidOutputReason: _ignored, ...row }) => row)
  const verdict = cleaned.every((result) => result.status === "pass") ? "pass" : "fail"
  const output: JsonValue = cleaned.map(rowToJsonValue)
  if (verdict === "fail") {
    const message = `Check verdict failure: ${checkFailureDetails(cleaned, checks)}`
    return { status: "fail", message, error: { code: "check-failed", message }, output }
  }
  return { status: "pass", output }
}

async function runOneCheck(
  check: CheckDeclaration,
  variables: JsonObject,
  deps: CheckExecutionDeps,
): Promise<CheckResultRow & { invalidOutputReason?: string }> {
  const resolved = deps.actions.resolve(check.uses)
  if (resolved.kind === "unknown") {
    return { name: check.name, status: "fail", message: `No action found for '${check.uses}'` }
  }
  if (resolved.kind === "tombstone") {
    return {
      name: check.name,
      status: "fail",
      message: `Check uses the removed Action '${check.uses}'. ${resolved.tombstone.guidance}`,
    }
  }
    const definition = resolved.definition
    try {
      const deferred = deferredInputFields(definition.manifest)
      const clonedWith = check.with ? structuredClone(check.with) : null
      const actionWith = injectEngineInputs(definition.manifest, clonedWith, variables)
      const unresolved = unresolvedReferences(removeDeferredFields(actionWith, deferred), variables)
    if (unresolved.length > 0) {
      return { name: check.name, status: "fail", message: deps.formatUnresolved(unresolved) }
    }
    const renderedWith = renderDeferred(actionWith, variables, deferred)
    const validation = validateActionInput(definition.manifest, renderedWith)
    if (validation.kind === "failure") {
      return {
        name: check.name,
        status: "fail",
        message: validation.error.message,
        error: validation.error,
      }
    }
    const workDir = await deps.resolveWorkDir(renderedWith)
    const caps = capabilitySet(definition.manifest)
    const host = deps.buildHost({ workId: "check", workType: "check" }, new AbortController().signal, workDir, caps)
    let rawResult: unknown
    try {
      rawResult = await definition.run(validation.input, host)
    } catch (thrown) {
      rawResult = malformedToUnexpectedError(
        `Action '${definition.manifest.name}' threw before returning a result: ${errorMessage(thrown)}`,
      )
    }
    const normalized = normalizeActionResult(rawResult, definition.manifest, caps)
    if (normalized.kind === "malformed") {
      if (normalized.reason === "output") {
        return {
          name: check.name,
          status: "fail",
          message: normalized.message,
          invalidOutputReason: normalized.message,
        }
      }
      const result = malformedToUnexpectedError(normalized.message)
      return {
        name: check.name,
        status: "fail",
        message: result.error?.message ?? normalized.message,
        error: result.error ?? { code: "unexpected-error", message: normalized.message },
      }
    }
    if (normalized.kind === "error") {
      return {
        name: check.name,
        status: "fail",
        message: normalized.error.message,
        error: normalized.error,
      }
    }
    if ((normalized.effects.addTasks?.length ?? 0) > 0 || Object.keys(normalized.effects.writeVars ?? {}).length > 0) {
      return {
        name: check.name,
        status: "fail",
        message: `Check produced unauthorized effects: effects are not permitted in checks`,
        error: { code: "unexpected-error", message: `Check produced unauthorized effects: effects are not permitted in checks` },
      }
    }
    return { name: check.name, status: "pass", output: normalized.output }
  } catch (error) {
    return {
      name: check.name,
      status: "fail",
      message: errorMessage(error),
      error: { code: "unexpected-error", message: errorMessage(error) },
    }
  }
}

function removeDeferredFields(withInput: JsonObject | null | undefined, deferred: Set<string>): JsonObject | null {
  if (!withInput) return null
  const immediate: JsonObject = {}
  for (const [key, value] of Object.entries(withInput)) {
    if (!deferred.has(key)) immediate[key] = value
  }
  return immediate
}

function renderDeferred(
  withInput: JsonObject | null | undefined,
  variables: JsonObject,
  deferred: Set<string>,
): JsonObject | null {
  return renderWithSkippedFields(withInput, variables, deferred)
}

function checkFailureDetails(
  results: ReadonlyArray<CheckResultRow>,
  checks: ReadonlyArray<CheckDeclaration>,
): string {
  return results
    .filter((r) => r.status === "fail")
    .map((c) => {
      const checkConfig = checks.find((ch) => ch.name === c.name)
      const isMarkerCheck = checkConfig?.uses === "core/marker"
      if (isMarkerCheck && checkConfig) {
        const expectedMarker = checkConfig.with?.expect ?? checkConfig.with?.contains ?? "PASS"
        return `${c.name}: expected verdict marker '${expectedMarker}' but it was not found in the artifact`
      }
      return `${c.name}: ${c.message}`
    })
    .join("; ")
}
