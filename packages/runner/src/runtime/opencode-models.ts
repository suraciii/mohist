import { runCommand } from '../system/process.js'
import { currentRunnerResources } from '../system/filesystem.js'
import { runnerLogger } from '../system/logger.js'

const log = runnerLogger.child('models')
const MODEL_HEADER = /^([^/\s]+)\/(\S+)$/
const DISCOVERY_TIMEOUT_MS = 3_000

export interface OpencodeModelCatalog {
  models: string[]
  variants: Record<string, string[]>
}

export interface DiscoveredOpencodeModels extends OpencodeModelCatalog {
  complete: boolean
}

export type OpencodeModelDiscovery = (signal: AbortSignal) => Promise<DiscoveredOpencodeModels>

export async function discoverOpencodeModels(signal: AbortSignal): Promise<DiscoveredOpencodeModels> {
  const resources = currentRunnerResources()
  if (resources?.opencodeModelDiscovery) return await resources.opencodeModelDiscovery(signal)

  const environment = resources?.environment ?? process.env
  const command = environment.MOHIST_AGENT_MODELS_COMMAND || environment.MOHIST_AGENT_COMMAND || 'opencode'
  try {
    const result = await runCommand(command, ['models', '--verbose'], '.', signal, undefined, {
      timeoutMs: DISCOVERY_TIMEOUT_MS,
    })
    const complete = result.status !== 'timeout'
    if (complete && result.exitCode !== 0) throw new Error(`${command} models --verbose exited with ${result.exitCode}`)

    const catalog = parseOpencodeModelsVerbose(result.stdout)
    if (catalog.models.length === 0) throw new Error('opencode models --verbose returned no valid model headers')
    if (!complete) log.warn('opencode model discovery timed out; using an incomplete catalog', { reason: 'timeout' })
    return { ...catalog, complete }
  } catch (error) {
    if (signal.aborted) throw error
    log.error('failed to discover opencode models', { exception: error })
    return { models: [], variants: {}, complete: false }
  }
}

export function parseOpencodeModelsVerbose(stdout: string): OpencodeModelCatalog {
  const models: string[] = []
  const variants: Record<string, string[]> = {}
  const lines = stdout.split(/\r?\n/)
  let index = 0

  while (index < lines.length) {
    const model = parseModelHeader(lines[index] ?? '')
    if (model === null) {
      index += 1
      continue
    }

    models.push(model)
    index += 1
    while (index < lines.length && (lines[index]?.trim() ?? '') === '') index += 1
    if (index >= lines.length || !(lines[index]?.trim() ?? '').startsWith('{')) continue

    const metadata = collectBalancedMetadata(lines, index)
    if (metadata === null) {
      index = findNextModelHeader(lines, index + 1)
      continue
    }
    const discoveredVariants = parseVariants(metadata.text)
    if (discoveredVariants.length > 0) variants[model] = discoveredVariants
    index = metadata.endLine + 1
  }

  return { models, variants }
}

export function mergeOpencodeModelCatalogs(
  current: OpencodeModelCatalog,
  discovered: OpencodeModelCatalog,
): OpencodeModelCatalog {
  const models = mergeStrings(current.models, discovered.models)
  const variants: Record<string, string[]> = {}
  for (const model of mergeStrings(Object.keys(current.variants), Object.keys(discovered.variants))) {
    const values = mergeStrings(current.variants[model] ?? [], discovered.variants[model] ?? [])
    if (values.length > 0) variants[model] = values
  }
  return { models, variants }
}

export function opencodeModelCatalogsEqual(left: OpencodeModelCatalog, right: OpencodeModelCatalog): boolean {
  if (!setsEqual(left.models, right.models)) return false
  const leftKeys = Object.keys(left.variants)
  const rightKeys = Object.keys(right.variants)
  return (
    setsEqual(leftKeys, rightKeys) &&
    leftKeys.every((model) => setsEqual(left.variants[model] ?? [], right.variants[model] ?? []))
  )
}

function parseModelHeader(line: string): string | null {
  const value = line.trim()
  return MODEL_HEADER.test(value) ? value : null
}

function collectBalancedMetadata(lines: string[], startLine: number): { text: string; endLine: number } | null {
  const parts: string[] = []
  let depth = 0
  let inString = false
  let escaped = false

  for (let lineIndex = startLine; lineIndex < lines.length; lineIndex += 1) {
    const line = lines[lineIndex] ?? ''
    for (let column = 0; column < line.length; column += 1) {
      const character = line[column]
      if (escaped) {
        escaped = false
      } else if (inString) {
        if (character === '\\') escaped = true
        else if (character === '"') inString = false
      } else if (character === '"') {
        inString = true
      } else if (character === '{') {
        depth += 1
      } else if (character === '}') {
        depth -= 1
        if (depth === 0) {
          parts.push(line.slice(0, column + 1))
          return { text: parts.join('\n'), endLine: lineIndex }
        }
      }
    }
    parts.push(line)
  }
  return null
}

function findNextModelHeader(lines: string[], startLine: number): number {
  for (let index = startLine; index < lines.length; index += 1) {
    if (parseModelHeader(lines[index] ?? '') !== null) return index
  }
  return lines.length
}

function parseVariants(metadata: string): string[] {
  try {
    const parsed = JSON.parse(metadata) as Record<string, unknown>
    const variants = parsed.variants
    if (variants === null || typeof variants !== 'object' || Array.isArray(variants)) return []
    return Object.keys(variants)
  } catch {
    return []
  }
}

function mergeStrings(current: readonly string[], discovered: readonly string[]): string[] {
  return [...new Set([...current, ...discovered])]
}

function setsEqual(left: readonly string[], right: readonly string[]): boolean {
  const leftSet = new Set(left)
  const rightSet = new Set(right)
  return leftSet.size === rightSet.size && [...leftSet].every((value) => rightSet.has(value))
}
