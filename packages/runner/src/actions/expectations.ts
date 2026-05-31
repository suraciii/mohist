import { join } from "node:path"
import type { ActionContext, JsonValue } from "../core/types.js"
import { arrayInput, isObject, objectInput } from "../core/json.js"
import { exists, readText } from "../system/process.js"

export interface TaskArtifactExpectation {
  satisfied: boolean
  missingFiles: Array<{ path: string }>
  missingArtifactMarkers: Array<{ path: string; contains: string }>
  message: string
}

export async function verifyExpectations(context: ActionContext): Promise<TaskArtifactExpectation> {
  const expect = objectInput(context.with, "expect")
  const files = arrayInput(expect, "files").filter(isObject)
  const markers = arrayInput(expect, "markers").filter(isObject)
  const missingFiles = files.map((file) => resolveActionPath(context, stringValue(file.path))).filter((path): path is string => !!path && !exists(path)).map((path) => ({ path }))
  const missingArtifactMarkers = (await Promise.all(markers.map(async (marker) => {
    const path = resolveActionPath(context, stringValue(marker.path))
    const contains = stringValue(marker.contains)
    if (!path || !contains || !exists(path)) return path && contains ? { path, contains } : null
    return (await readText(path)).includes(contains) ? null : { path, contains }
  }))).filter((marker): marker is { path: string; contains: string } => marker !== null)
  return {
    satisfied: missingFiles.length === 0 && missingArtifactMarkers.length === 0,
    missingFiles,
    missingArtifactMarkers,
    message: missingFiles.length === 0 && missingArtifactMarkers.length === 0 ? "Agent completion requirements satisfied" : `Agent completion requirements were not satisfied: ${[...missingFiles.map((file) => `missing artifact file: ${file.path}`), ...missingArtifactMarkers.map((marker) => `missing artifact marker in ${marker.path}: ${marker.contains}`)].join("; ")}`,
  }
}

export function resolveActionPath(context: ActionContext, value?: string) {
  if (!value) return undefined
  return value.match(/^[A-Za-z]:[\\/]|^\//) ? value : join(context.workDir, value)
}

function stringValue(value: JsonValue | undefined) {
  return typeof value === "string" ? value : undefined
}
