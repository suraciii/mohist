import { join } from "node:path"
import type { ActionContext, JsonValue } from "../core/types.js"
import { arrayInput, isObject, objectInput, stringInput } from "../core/json.js"
import { exists, readText } from "../system/process.js"

export interface FailIfMatch {
  marker: string
  failIf: string
  path: string
  errorCode: string | null
}

export interface TaskArtifactExpectation {
  satisfied: boolean
  matched?: string
  missingFiles: Array<{ path: string }>
  missingArtifactMarkers: Array<{ path: string; contains: string }>
  failIfMatches: FailIfMatch[]
  message: string
}

const OUTPUT_MARKER_PATH = "_output"

export async function verifyExpectations(context: ActionContext, agentText?: string): Promise<TaskArtifactExpectation> {
  const expect = objectInput(context.with, "expect")
  const files = arrayInput(expect, "files").filter(isObject)
  const markers = arrayInput(expect, "markers").filter(isObject)
  const missingFiles = files.map((file) => resolveActionPath(context, stringValue(file.path))).filter((path): path is string => !!path && !exists(path)).map((path) => ({ path }))

  let matched: string | undefined
  const missingArtifactMarkers: Array<{ path: string; contains: string }> = []
  const failIfMatches: FailIfMatch[] = []

  for (const marker of markers) {
    const rawPath = stringValue(marker.path)
    const accepted = resolveAcceptedMarkers(marker)
    const failIf = resolveFailIf(marker)
    if (accepted.length === 0) continue

    if (rawPath === OUTPUT_MARKER_PATH) {
      if (!agentText) {
        missingArtifactMarkers.push({ path: OUTPUT_MARKER_PATH, contains: formatAcceptedMarkers(accepted) })
        continue
      }
      const last = parseLastMarker(agentText)
      if (last && accepted.includes(last)) {
        matched = last
        if (failIf && last === failIf) {
          failIfMatches.push({ marker: last, failIf, path: OUTPUT_MARKER_PATH, errorCode: extractErrorCode(agentText) })
        }
        continue
      }
      missingArtifactMarkers.push({ path: OUTPUT_MARKER_PATH, contains: formatAcceptedMarkers(accepted) })
      continue
    }

    const path = resolveActionPath(context, rawPath)
    if (!path) continue
    if (!exists(path)) {
      missingArtifactMarkers.push({ path, contains: formatAcceptedMarkers(accepted) })
      continue
    }
    const content = await readText(path)
    const hit = accepted.find((value) => content.includes(value))
    if (hit) {
      matched = hit
      if (failIf && hit === failIf) {
        failIfMatches.push({ marker: hit, failIf, path, errorCode: extractErrorCode(content) })
      }
      continue
    }
    missingArtifactMarkers.push({ path, contains: formatAcceptedMarkers(accepted) })
  }

  return {
    satisfied: missingFiles.length === 0 && missingArtifactMarkers.length === 0 && failIfMatches.length === 0,
    matched,
    missingFiles,
    missingArtifactMarkers,
    failIfMatches,
    message: buildMessage(missingFiles, missingArtifactMarkers, failIfMatches),
  }
}

function buildMessage(missingFiles: Array<{ path: string }>, missingArtifactMarkers: Array<{ path: string; contains: string }>, failIfMatches: FailIfMatch[]): string {
  if (missingFiles.length === 0 && missingArtifactMarkers.length === 0 && failIfMatches.length === 0) {
    return "Agent completion requirements satisfied"
  }
  const parts: string[] = []
  for (const file of missingFiles) parts.push(`missing artifact file: ${file.path}`)
  for (const marker of missingArtifactMarkers) parts.push(`missing artifact marker in ${marker.path}: ${marker.contains}`)
  for (const fail of failIfMatches) parts.push(`failIf marker matched in ${fail.path}: ${fail.marker} (errorCode: ${fail.errorCode ?? "<none>"})`)
  return `Agent completion requirements were not satisfied: ${parts.join("; ")}`
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

export function resolveActionPath(context: ActionContext, value?: string) {
  if (!value) return undefined
  return value.match(/^[A-Za-z]:[\\/]|^\//) ? value : join(context.workDir, value)
}

function stringValue(value: JsonValue | undefined) {
  return typeof value === "string" ? value : undefined
}

/**
 * Extract an `errorCode: <value>` declaration from an artifact body. The
 * convention mirrors how the action can publish its own errorCode alongside
 * a verdict marker (e.g. `errorCode: review-failed` in a review file). The
 * value must be a single line — no inline stripping of surrounding markdown.
 */
export function extractErrorCode(content: string): string | null {
  if (!content) return null
  const lines = content.split(/\r?\n/)
  for (const raw of lines) {
    const line = raw.trim()
    if (!line.startsWith("errorCode")) continue
    const colon = line.indexOf(":")
    if (colon < 0) continue
    const value = line.slice(colon + 1).trim()
    if (!value) continue
    if (value.length > 256) continue
    return value
  }
  return null
}