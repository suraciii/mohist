import { join } from "node:path"
import type { JsonObject, JsonValue } from "../core/types.js"
import { isObject, stringInput } from "../core/json.js"
import { exists, readText } from "../system/process.js"

export interface FailIfMatch {
  marker: string
  failIf: string
  path: string
}

export interface CompletionEvaluation {
  satisfied: boolean
  matched?: string
  missingFiles: Array<{ path: string }>
  missingMarkers: Array<{ path: string; contains: string }>
  failIfMatches: FailIfMatch[]
  message: string
}

const OUTPUT_MARKER_PATH = "_output"

/**
 * Workflow-owned completion evaluator. Reads the expanded task-level
 * `expect` declaration (a JSON object), checks declared files exist,
 * matches file-backed markers against configured `oneOf` / `contains`
 * values, applies `failIf`, and evaluates the `_output` private
 * fact-channel marker against the turn's final assistant text.
 *
 * The completion evaluator is intentionally agnostic to which Action
 * returned. It does NOT read `ActionContext.with.expect` (the Action
 * never receives `expect`); the executor passes the rendered `expect`
 * object directly.
 *
 * Marker precedence is asymmetric by design:
 *  - File-backed markers: the matched value is the first present value
 *    in `oneOf` declaration order.
 *  - `_output` markers: the matched value is the last accepted
 *    occurrence in the assistant text (so later verdicts override
 *    earlier ones; the final answer wins).
 *
 * Missing files or unsatisfied markers fail the task with a
 * human-readable `message` that the runner returns to the server.
 */
export async function evaluateCompletion(
  expect: JsonObject | null | undefined,
  workDir: string,
  finalAssistantText?: string | null,
): Promise<CompletionEvaluation> {
  const files = readFilesArray(expect)
  const markers = readMarkersArray(expect)
  const missingFiles: Array<{ path: string }> = []
  for (const file of files) {
    const path = typeof file?.path === "string" ? resolveLocalPath(workDir, file.path) : null
    if (!path) continue
    if (!exists(path)) missingFiles.push({ path })
  }

  let matched: string | undefined
  const missingMarkers: Array<{ path: string; contains: string }> = []
  const failIfMatches: FailIfMatch[] = []

  for (const marker of markers) {
    if (!isObject(marker)) continue
    const rawPath = typeof marker.path === "string" ? marker.path : undefined
    const accepted = resolveAcceptedMarkers(marker)
    if (accepted.length === 0) continue
    const failIf = resolveFailIf(marker)

    if (rawPath === OUTPUT_MARKER_PATH) {
      const last = finalAssistantText ? parseLastMarker(finalAssistantText, accepted) : null
      if (last && accepted.includes(last)) {
        matched = last
        if (failIf && last === failIf) {
          failIfMatches.push({ marker: last, failIf, path: OUTPUT_MARKER_PATH })
        }
        continue
      }
      missingMarkers.push({ path: OUTPUT_MARKER_PATH, contains: formatAcceptedMarkers(accepted) })
      continue
    }

    const path = rawPath ? resolveLocalPath(workDir, rawPath) : null
    if (!path) continue
    if (!exists(path)) {
      missingMarkers.push({ path, contains: formatAcceptedMarkers(accepted) })
      continue
    }
    const content = await readText(path)
    const hit = accepted.find((value) => content.includes(value))
    if (hit) {
      matched = hit
      if (failIf && hit === failIf) {
        failIfMatches.push({ marker: hit, failIf, path })
      }
      continue
    }
    missingMarkers.push({ path, contains: formatAcceptedMarkers(accepted) })
  }

  const satisfied = missingFiles.length === 0 && missingMarkers.length === 0 && failIfMatches.length === 0
  return {
    satisfied,
    matched,
    missingFiles,
    missingMarkers,
    failIfMatches,
    message: buildMessage(missingFiles, missingMarkers, failIfMatches),
  }
}

function readFilesArray(expect: JsonObject | null | undefined) {
  const raw = expect?.files
  return Array.isArray(raw) ? raw.filter(isObject) : []
}

function readMarkersArray(expect: JsonObject | null | undefined) {
  const raw = expect?.markers
  return Array.isArray(raw) ? raw.filter(isObject) : []
}

function buildMessage(
  missingFiles: Array<{ path: string }>,
  missingMarkers: Array<{ path: string; contains: string }>,
  failIfMatches: FailIfMatch[],
): string {
  if (missingFiles.length === 0 && missingMarkers.length === 0 && failIfMatches.length === 0) {
    return "Workflow completion requirements satisfied"
  }
  const parts: string[] = []
  for (const file of missingFiles) parts.push(`missing required file: ${file.path}`)
  for (const marker of missingMarkers) parts.push(`missing marker in ${marker.path}: ${marker.contains}`)
  for (const fail of failIfMatches) parts.push(`failIf marker matched in ${fail.path}: ${fail.marker}`)
  return `Workflow completion requirements were not satisfied: ${parts.join("; ")}`
}

/**
 * Generalized `<promise>VALUE</promise>` parser. Returns the last
 * occurrence whose `VALUE` is one of the marker's accepted values, or
 * null if no accepted occurrence is present. The legacy parser only
 * recognized lowercase `done|unfinished`; this version accepts
 * arbitrary `VALUE`s declared in the marker's `oneOf` (or `contains`)
 * list, matching the spec scenario for arbitrary `<promise>` markers.
 *
 * `accepted` lets callers restrict the parser to the marker's
 * configured value set so a stray `<promise>other</promise>` in the
 * text does not falsely match. The bare marker
 * `<promise>VALUE</promise>` is returned (caller compares against
 * `accepted.includes(...)`, mirroring the file-marker shape).
 */
export function parseLastMarker(text: string, accepted: string[]): string | null {
  if (!text || accepted.length === 0) return null
  const matches = [...text.matchAll(/<promise>\s*([^<>\s]+)\s*<\/promise>/g)]
  if (matches.length === 0) return null
  for (let i = matches.length - 1; i >= 0; i -= 1) {
    const value = matches[i][1]
    const marker = `<promise>${value}</promise>`
    if (accepted.includes(marker)) return marker
  }
  return null
}

function resolveAcceptedMarkers(marker: JsonValue): string[] {
  if (!isObject(marker)) return []
  const oneOfRaw = marker.oneOf
  if (Array.isArray(oneOfRaw) && oneOfRaw.length > 0) {
    const values = oneOfRaw.filter((value): value is string => typeof value === "string" && value.length > 0)
    if (values.length > 0) return values
  }
  const contains = stringInput(marker, "contains")
  return contains ? [contains] : []
}

function resolveFailIf(marker: JsonValue): string | null {
  if (!isObject(marker)) return null
  const value = marker.failIf
  if (typeof value !== "string" || value.length === 0) return null
  return value
}

function formatAcceptedMarkers(values: string[]): string {
  if (values.length === 1) return values[0]
  return `oneOf: ${values.join(" | ")}`
}

function resolveLocalPath(workDir: string, value: string): string {
  return value.match(/^[A-Za-z]:[\\/]|^\//) ? value : join(workDir, value)
}

/**
 * Extract the bare verdict from a `<promise>VALUE</promise>` marker —
 * e.g. "PASS", "FAIL", "done", "unfinished". Returns null when the
 * marker is absent or not a promise marker. The mohist/opencode Action
 * uses this to project the matched value into its
 * `{ "promise": "<value>" }` output so workflow onFailure cases can
 * match on the verdict the agent actually produced.
 */
export function promiseValue(marker: string | undefined | null): string | null {
  if (!marker) return null
  const match = marker.match(/^<promise>\s*([^<>\s]+)\s*<\/promise>$/)
  return match ? match[1] : null
}

/**
 * Resolve a relative path against the action's working directory.
 * Absolute paths (including POSIX `/` and Windows drive-letter forms)
 * pass through unchanged.
 */
export function resolveActionPath(workDir: string, value?: string) {
  if (!value) return undefined
  return value.match(/^[A-Za-z]:[\\/]|^\//) ? value : join(workDir, value)
}
