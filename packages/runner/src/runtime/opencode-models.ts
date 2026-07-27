import { execFile } from "node:child_process"
import type { ExecFileException, ExecFileOptionsWithStringEncoding } from "node:child_process"
import { assertExternalProcessAllowed } from "../system/process-policy.js"

export interface OpencodeModelCatalog {
  models: string[]
  variants: Record<string, string[]>
}

export interface DiscoveredOpencodeModels extends OpencodeModelCatalog {
  complete: boolean
}

export type OpencodeModelDiscovery = (
  signal: AbortSignal,
) => Promise<DiscoveredOpencodeModels>

interface ModelsProcessResult {
  error?: ExecFileException
  status: number | null
  stdout: string
}

export type ModelsProcessExecutor = (
  command: string,
  args: readonly string[],
  options: ExecFileOptionsWithStringEncoding,
) => ModelsProcessResult | Promise<ModelsProcessResult>

export interface OpencodeModelsCommandAdapter {
  execute(command: string, signal: AbortSignal): Promise<{
    stdout: string
    complete: boolean
  }>
}

const MODEL_HEADER = /^([^/\s]+)\/(\S+)$/
const MODEL_DISCOVERY_TIMEOUT_MS = 3_000

export function createOpencodeModelsCommandAdapter(
  executor: ModelsProcessExecutor,
): OpencodeModelsCommandAdapter {
  const checksExternalProcessPolicy = executor === executeModelsProcess
  return {
    async execute(command, signal) {
      if (checksExternalProcessPolicy) {
        assertExternalProcessAllowed("runtime/opencode-models.execute")
      }
      const args = ["models", "--verbose"]
      const result = await executor(command, args, {
        signal,
        timeout: MODEL_DISCOVERY_TIMEOUT_MS,
        encoding: "utf8",
        maxBuffer: 16 * 1024 * 1024,
      })
      const errorCode = result.error && "code" in result.error ? result.error.code : undefined
      if (result.error) {
        if (errorCode === "ETIMEDOUT" && result.stdout.trim().length > 0) {
          return { stdout: result.stdout, complete: false }
        }
        throw result.error
      }
      if (result.status !== 0) {
        throw new Error(`${command} ${args.join(" ")} exited with ${result.status}`)
      }
      return { stdout: result.stdout, complete: true }
    },
  }
}

const productionCommandAdapter = createOpencodeModelsCommandAdapter(executeModelsProcess)

export async function discoverOpencodeModels(
  signal: AbortSignal,
  commandAdapter: OpencodeModelsCommandAdapter = productionCommandAdapter,
): Promise<DiscoveredOpencodeModels> {
  const command = process.env.MOHIST_AGENT_MODELS_COMMAND || process.env.MOHIST_AGENT_COMMAND || "opencode"
  try {
    const output = await commandAdapter.execute(command, signal)
    const catalog = parseOpencodeModelsVerbose(output.stdout)
    if (catalog.models.length === 0) {
      throw new Error("opencode models --verbose returned no valid model headers")
    }
    if (!output.complete) {
      console.warn("opencode model discovery timed out; using an incomplete catalog")
    }
    return { ...catalog, complete: output.complete }
  } catch (error) {
    console.error("failed to discover opencode models", error)
    return { models: [], variants: {}, complete: false }
  }
}

let opencodeModelDiscovery: OpencodeModelDiscovery = discoverOpencodeModels

export function getOpencodeModelDiscovery(): OpencodeModelDiscovery {
  return opencodeModelDiscovery
}

export function setOpencodeModelDiscoveryForTest(discovery: OpencodeModelDiscovery | null): void {
  opencodeModelDiscovery = discovery ?? discoverOpencodeModels
}

export function parseOpencodeModelsVerbose(stdout: string): OpencodeModelCatalog {
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
  left: OpencodeModelCatalog,
  right: OpencodeModelCatalog,
): boolean {
  if (!stringSetsEqual(left.models, right.models)) return false
  const leftVariantKeys = Object.keys(left.variants)
  const rightVariantKeys = Object.keys(right.variants)
  if (!stringSetsEqual(leftVariantKeys, rightVariantKeys)) return false
  return leftVariantKeys.every((model) => stringSetsEqual(left.variants[model] ?? [], right.variants[model] ?? []))
}

export function mergeOpencodeModelCatalogs(
  current: OpencodeModelCatalog,
  discovered: OpencodeModelCatalog,
): OpencodeModelCatalog {
  const models = mergeStringSets(current.models, discovered.models)
  const variants: Record<string, string[]> = {}
  const variantModels = mergeStringSets(Object.keys(current.variants), Object.keys(discovered.variants))
  for (const model of variantModels) {
    const merged = mergeStringSets(current.variants[model] ?? [], discovered.variants[model] ?? [])
    if (merged.length > 0) variants[model] = merged
  }
  return { models, variants }
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

function mergeStringSets(current: readonly string[], discovered: readonly string[]): string[] {
  const merged = [...current]
  const seen = new Set(current)
  for (const value of discovered) {
    if (seen.has(value)) continue
    seen.add(value)
    merged.push(value)
  }
  return merged
}

function executeModelsProcess(
  command: string,
  args: readonly string[],
  options: ExecFileOptionsWithStringEncoding,
): Promise<ModelsProcessResult> {
  return new Promise((resolve) => {
    execFile(command, args, options, (error, stdout) => {
      resolve({
        error: normalizeProcessError(error, options.signal),
        status: error && typeof error.code === "number" ? error.code : error ? null : 0,
        stdout,
      })
    })
  })
}

function normalizeProcessError(
  error: ExecFileException | null,
  signal: AbortSignal | undefined,
): ExecFileException | undefined {
  if (!error) return undefined
  if (error.killed && error.signal === "SIGTERM" && error.code === null && !signal?.aborted) {
    return Object.assign(new Error(error.message, { cause: error }), { code: "ETIMEDOUT" })
  }
  return error
}
