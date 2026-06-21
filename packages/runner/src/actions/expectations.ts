import { join } from "node:path"
import type { ActionContext, JsonValue } from "../core/types.js"
import { arrayInput, isObject, objectInput, stringInput } from "../core/json.js"
import { exists, readText } from "../system/process.js"

export interface TaskArtifactExpectation {
  satisfied: boolean
  matched?: string
  missingFiles: Array<{ path: string }>
  missingArtifactMarkers: Array<{ path: string; contains: string }>
  message: string
}

const OUTPUT_MARKER_PATH = "_output"

export async function verifyExpectations(context: ActionContext, agentText?: string): Promise<TaskArtifactExpectation> {
  const expect = objectInput(context.with, "expect")
  const files = arrayInput(expect, "files").filter(isObject)
  const markers = arrayInput(expect, "markers").filter(isObject)
  const missingFiles = files.map((file) => resolveActionPath(context, stringValue(file.path))).filter((path): path is string => !!path && !exists(path)).map((path) => ({ path }))

  let matched: string | undefined
  const missingArtifactMarkers = (await Promise.all(markers.map(async (marker) => {
    const rawPath = stringValue(marker.path)
    const accepted = resolveAcceptedMarkers(marker)
    if (accepted.length === 0) return null

    if (rawPath === OUTPUT_MARKER_PATH) {
      if (!agentText) return { path: OUTPUT_MARKER_PATH, contains: formatAcceptedMarkers(accepted) }
      const last = parseLastMarker(agentText)
      if (last && accepted.includes(last)) {
        matched = last
        return null
      }
      return { path: OUTPUT_MARKER_PATH, contains: formatAcceptedMarkers(accepted) }
    }

    const path = resolveActionPath(context, rawPath)
    if (!path) return null
    if (!exists(path)) return { path, contains: formatAcceptedMarkers(accepted) }
    const content = await readText(path)
    return accepted.some((value) => content.includes(value)) ? null : { path, contains: formatAcceptedMarkers(accepted) }
  }))).filter((marker): marker is { path: string; contains: string } => marker !== null)
  return {
    satisfied: missingFiles.length === 0 && missingArtifactMarkers.length === 0,
    matched,
    missingFiles,
    missingArtifactMarkers,
    message: missingFiles.length === 0 && missingArtifactMarkers.length === 0 ? "Agent completion requirements satisfied" : `Agent completion requirements were not satisfied: ${[...missingFiles.map((file) => `missing artifact file: ${file.path}`), ...missingArtifactMarkers.map((marker) => `missing artifact marker in ${marker.path}: ${marker.contains}`)].join("; ")}`,
  }
}

function parseLastMarker(text: string): string | null {
  const matches = [...text.matchAll(/<promise>\s*(done|unfinished)\s*<\/promise>/g)]
  if (matches.length === 0) return null
  const last = matches[matches.length - 1]
  return `<promise>${last[1]}</promise>`
}

function resolveAcceptedMarkers(marker: JsonValue): string[] {
  if (!isObject(marker)) return []
  const oneOf = arrayInput(marker, "oneOf")
  if (oneOf.length > 0) {
    const values = oneOf.filter((value): value is string => typeof value === "string" && value.length > 0)
    if (values.length > 0) return values
  }
  const contains = stringInput(marker, "contains")
  return contains ? [contains] : []
}

function formatAcceptedMarkers(values: string[]): string {
  if (values.length === 1) return values[0]
  return `oneOf: ${values.join(" | ")}`
}

export function resolveActionPath(context: ActionContext, value?: string) {
  if (!value) return undefined
  return value.match(/^[A-Za-z]:[\\/]|^\//) ? value : join(context.workDir, value)
}

function stringValue(value: JsonValue | undefined) {
  return typeof value === "string" ? value : undefined
}
