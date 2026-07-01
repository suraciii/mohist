import type { JsonObject } from "../core/types.js"

export interface CheckResultRow {
  name?: string
  status: string
  message?: string | null
}

interface CheckDeclaration {
  name?: string
  uses: string
  with?: JsonObject | null
}

// Format the per-failed-check details for a `Check verdict failure: ...` message.
// For `core/marker` checks the runner renders a verdict-marker-specific hint
// using the original `with.expect`/`with.contains` from the dispatch.
export function checkFailureDetails(
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
