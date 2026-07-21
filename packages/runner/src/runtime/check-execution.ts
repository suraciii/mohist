import type { ActionError, JsonObject, JsonValue, WorkItemResult } from "../core/types.js"
import type { ActionInvocationContext, ValidatedActionContext } from "../actions/context.js"
import { renderTemplate, wholeStringUnresolvedReferences } from "../core/template.js"
import type { ActionRegistry } from "../actions/registry.js"
import { validateActionInput } from "../actions/input-validation.js"
import { malformedToUnexpectedError, normalizeActionResult } from "../actions/result-validation.js"
import { errorMessage } from "../core/errors.js"
import { isActionFailure } from "../actions/action-result.js"
import { workflowSessionName } from "../actions/workflow-session-name.js"
import type { WorkflowSessionTurnCoordinator } from "./workflow-session-turn-coordinator.js"

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
  context: Omit<ActionInvocationContext, "with" | "workDir">
  coordinator?: WorkflowSessionTurnCoordinator
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
    const unresolved = wholeStringUnresolvedReferences(check.with ?? null, variables)
    if (unresolved.length > 0) {
      return { name: check.name, status: "fail", message: deps.formatUnresolved(unresolved) }
    }
    const renderedWith = renderTemplate(check.with ?? null, variables)
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
    const runAction = async (): Promise<CheckResultRow & { invalidOutputReason?: string }> => {
      let rawResult: unknown
      try {
        const actionContext: ActionInvocationContext = {
          ...deps.context,
          workType: "check",
          title: check.title,
          uses: check.uses,
          with: validation.input,
          workDir,
        }
        rawResult = await definition.run(actionContext as ValidatedActionContext)
      } catch (thrown) {
        rawResult = malformedToUnexpectedError(
          `Action '${definition.manifest.name}' threw before returning a result: ${errorMessage(thrown)}`,
        )
      }
      const normalized = normalizeActionResult(rawResult, definition.manifest)
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
      return { name: check.name, status: "pass", output: normalized.output }
    }
    const result = isInlineAgentAction(check.uses) && deps.coordinator
      ? await deps.coordinator.withTurn(
        {
          projectId: deps.context.projectId ?? "",
          workflowRunId: deps.context.workflowRunId,
          sessionName: workflowSessionName(renderedWith, deps.context.workId),
        },
        runAction,
      )
      : await runAction()
    return result
  } catch (error) {
    return {
      name: check.name,
      status: "fail",
      message: errorMessage(error),
      error: { code: "unexpected-error", message: errorMessage(error) },
    }
  }
}

function isInlineAgentAction(uses: string): boolean {
  const normalized = uses.trim().toLowerCase()
  return normalized === "mohist/opencode" || normalized === "mohist/pi"
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
