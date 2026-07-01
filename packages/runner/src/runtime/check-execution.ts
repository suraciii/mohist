import type { ActionContext, JsonObject, WorkItemResult } from "../core/types.js"
import { renderTemplate, wholeStringUnresolvedReferences } from "../core/template.js"
import type { ActionRegistry } from "../actions/registry.js"

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
  output?: string | null
}

export interface CheckExecutionDeps {
  actions: ActionRegistry
  context: Omit<ActionContext, "with" | "workDir">
  formatUnresolved: (unresolved: string[]) => string
  resolveWorkDir: (withInput: JsonObject | null) => Promise<string>
  toCheckStatus: (status: string) => string
}

export async function executeCheckDispatch(
  checks: CheckDeclaration[],
  variables: JsonObject,
  deps: CheckExecutionDeps,
): Promise<WorkItemResult> {
  if (checks.length === 0) return { status: "fail", message: "No checks found in dispatch" }

  const results = await Promise.all(checks.map((check) => runOneCheck(check, variables, deps)))
  const verdict = results.every((result) => result.status === "pass") ? "pass" : "fail"
  const output = JSON.stringify(results)
  if (verdict === "fail") {
    return { status: "fail", message: `Check verdict failure: ${checkFailureDetails(results, checks)}`, output }
  }
  return { status: "pass", output }
}

async function runOneCheck(
  check: CheckDeclaration,
  variables: JsonObject,
  deps: CheckExecutionDeps,
): Promise<CheckResultRow> {
  const action = deps.actions.resolve(check.uses)
  if (!action) return { name: check.name, status: "fail", message: `No action found for '${check.uses}'` }
  try {
    const unresolved = wholeStringUnresolvedReferences(check.with ?? null, variables)
    if (unresolved.length > 0) {
      return { name: check.name, status: "fail", message: deps.formatUnresolved(unresolved) }
    }
    const renderedWith = renderTemplate(check.with ?? null, variables)
    const workDir = await deps.resolveWorkDir(renderedWith)
    const result = await action({ ...deps.context, workType: "check", title: check.title, uses: check.uses, with: renderedWith, workDir })
    return { name: check.name, status: deps.toCheckStatus(result.status), message: result.message, output: result.output }
  } catch (error) {
    return { name: check.name, status: "fail", message: error instanceof Error ? error.message : String(error) }
  }
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
