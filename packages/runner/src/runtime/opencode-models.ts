import { spawnSync } from "node:child_process"
import type { SpawnSyncOptionsWithStringEncoding, SpawnSyncReturns } from "node:child_process"
import { assertExternalProcessAllowed } from "../system/process-policy.js"

export interface DiscoveredOpencodeModels {
  models: string[]
  variants: Record<string, string[]>
}

export type SyncModelsProcessExecutor = (
  command: string,
  args: readonly string[],
  options: SpawnSyncOptionsWithStringEncoding,
) => Pick<SpawnSyncReturns<string>, "error" | "status" | "stdout">

export interface OpencodeModelsCommandAdapter {
  execute(command: string, signal: AbortSignal): string
}

const MODEL_HEADER = /^([^/\s]+)\/(\S+)$/

export function createOpencodeModelsCommandAdapter(
  executor: SyncModelsProcessExecutor,
): OpencodeModelsCommandAdapter {
  const checksExternalProcessPolicy = executor === spawnSync
  return {
    execute(command, signal) {
      if (checksExternalProcessPolicy) {
        assertExternalProcessAllowed("runtime/opencode-models.execute")
      }
      const args = ["models", "--verbose"]
      const result = executor(command, args, {
        signal,
        timeout: 10_000,
        encoding: "utf8",
        maxBuffer: 16 * 1024 * 1024,
      })
      if (result.error) throw result.error
      if (result.status !== 0) {
        throw new Error(`${command} ${args.join(" ")} exited with ${result.status}`)
      }
      return result.stdout
    },
  }
}

const productionCommandAdapter = createOpencodeModelsCommandAdapter(spawnSync)

export async function discoverOpencodeModels(
  signal: AbortSignal,
  commandAdapter: OpencodeModelsCommandAdapter = productionCommandAdapter,
): Promise<DiscoveredOpencodeModels> {
  const command = process.env.MOHIST_AGENT_MODELS_COMMAND || process.env.MOHIST_AGENT_COMMAND || "opencode"
  try {
    const catalog = parseOpencodeModelsVerbose(commandAdapter.execute(command, signal))
    if (catalog.models.length === 0) {
      throw new Error("opencode models --verbose returned no valid model headers")
    }
    return catalog
  } catch (error) {
    console.error("failed to discover opencode models", error)
    return { models: [], variants: {} }
  }
}

export function parseOpencodeModelsVerbose(stdout: string): DiscoveredOpencodeModels {
  const models: string[] = []
  const variants: Record<string, string[]> = {}
  const lines = stdout.split(/\r?\n/)
  let index = 0

  while (index < lines.length) {
    const model = parseModelHeader(lines[index] ?? "")
    if (model === null) {
      index += 1
      continue
    }

    models.push(model)
    index += 1
    while (index < lines.length && (lines[index]?.trim() ?? "") === "") index += 1
    if (index >= lines.length || !(lines[index]?.trim() ?? "").startsWith("{")) continue

    const metadataStart = index
    const metadata = collectBalancedMetadata(lines, metadataStart)
    if (metadata === null) {
      index = findNextModelHeader(lines, metadataStart + 1)
      continue
    }

    const modelVariants = parseVariants(metadata.text)
    if (modelVariants.length > 0) variants[model] = modelVariants
    index = metadata.endLine + 1
  }

  return { models, variants }
}

export function opencodeModelSetsEqual(
  left: DiscoveredOpencodeModels,
  right: DiscoveredOpencodeModels,
): boolean {
  if (!stringSetsEqual(left.models, right.models)) return false
  const leftVariantKeys = Object.keys(left.variants)
  const rightVariantKeys = Object.keys(right.variants)
  if (!stringSetsEqual(leftVariantKeys, rightVariantKeys)) return false
  return leftVariantKeys.every((model) => stringSetsEqual(left.variants[model] ?? [], right.variants[model] ?? []))
}

function parseModelHeader(line: string): string | null {
  const trimmed = line.trim()
  return MODEL_HEADER.test(trimmed) ? trimmed : null
}

function collectBalancedMetadata(
  lines: string[],
  startLine: number,
): { text: string; endLine: number } | null {
  const parts: string[] = []
  let depth = 0
  let inString = false
  let escaped = false

  for (let lineIndex = startLine; lineIndex < lines.length; lineIndex += 1) {
    const line = lines[lineIndex] ?? ""
    for (let column = 0; column < line.length; column += 1) {
      const character = line[column]
      if (escaped) {
        escaped = false
        continue
      }
      if (inString) {
        if (character === "\\") escaped = true
        else if (character === '"') inString = false
        continue
      }
      if (character === '"') inString = true
      else if (character === "{") depth += 1
      else if (character === "}") {
        depth -= 1
        if (depth === 0) {
          parts.push(line.slice(0, column + 1))
          return { text: parts.join("\n"), endLine: lineIndex }
        }
      }
    }
    parts.push(line)
  }

  return null
}

function findNextModelHeader(lines: string[], startLine: number): number {
  for (let index = startLine; index < lines.length; index += 1) {
    if (parseModelHeader(lines[index] ?? "") !== null) return index
  }
  return lines.length
}

function parseVariants(metadata: string): string[] {
  try {
    const parsed = JSON.parse(metadata) as unknown
    if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) return []
    const value = (parsed as Record<string, unknown>)["variants"]
    if (value === null || typeof value !== "object" || Array.isArray(value)) return []
    return Object.keys(value)
  } catch {
    return []
  }
}

function stringSetsEqual(left: string[], right: string[]): boolean {
  const leftSet = new Set(left)
  const rightSet = new Set(right)
  if (leftSet.size !== rightSet.size) return false
  return [...leftSet].every((value) => rightSet.has(value))
}
