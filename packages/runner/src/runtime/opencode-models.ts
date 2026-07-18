import { spawnSync } from "node:child_process"
import { assertExternalProcessAllowed } from "../system/process-policy.js"

export interface DiscoveredOpencodeModels {
  models: string[]
  variants: Record<string, string[]>
}

type ModelsCommandRunner = (command: string, args: string[], signal: AbortSignal) => Promise<string>

let runModelsCommand: ModelsCommandRunner = execFileText

export function setModelsCommandRunnerForTest(runner: ModelsCommandRunner | null): void {
  runModelsCommand = runner ?? execFileText
}

export async function discoverOpencodeModels(signal: AbortSignal): Promise<DiscoveredOpencodeModels> {
  const command = process.env.MOHIST_AGENT_MODELS_COMMAND ?? process.env.MOHIST_AGENT_COMMAND ?? "opencode"

  try {
    const stdout = await runModelsCommand(command, ["models", "--verbose"], signal)
    return parseOpencodeModelsVerbose(stdout)
  } catch (error) {
    console.error("failed to discover opencode models", error)
    return { models: [], variants: {} }
  }
}

export function opencodeModelSetsEqual(a: DiscoveredOpencodeModels, b: DiscoveredOpencodeModels): boolean {
  if (!stringSetsEqual(a.models, b.models)) return false

  const aVariantKeys = Object.keys(a.variants)
  const bVariantKeys = Object.keys(b.variants)
  if (!stringSetsEqual(aVariantKeys, bVariantKeys)) return false

  return aVariantKeys.every((key) => stringSetsEqual(a.variants[key] ?? [], b.variants[key] ?? []))
}

function stringSetsEqual(a: string[], b: string[]): boolean {
  if (a.length !== b.length) return false
  const sortedA = [...a].sort()
  const sortedB = [...b].sort()
  return sortedA.every((value, index) => value === sortedB[index])
}

export function parseOpencodeModelsVerbose(stdout: string): DiscoveredOpencodeModels {
  const models: string[] = []
  const variants: Record<string, string[]> = {}
  const lines = stdout.split(/\r?\n/)

  let i = 0
  while (i < lines.length) {
    const id = lines[i]?.trim() ?? ""
    i += 1
    if (id.length === 0) continue

    let jsonText: string | null = null
    let consumed = 0
    let jsonStartsAt = -1
    while (i < lines.length && (lines[i]?.trim() ?? "") === "") i += 1
    if (i < lines.length && (lines[i]?.trim() ?? "").startsWith("{")) {
      jsonStartsAt = i
      jsonText = collectBalancedJson(lines, i)
      if (jsonText !== null) {
        consumed = countConsumedLines(jsonText)
      }
    }

    models.push(id)
    if (jsonText !== null) {
      const parsed = parseVariants(jsonText)
      if (parsed.length > 0) variants[id] = parsed
      i += consumed
    } else if (jsonStartsAt >= 0) {
      i = jsonStartsAt + 1
    }
  }

  return { models, variants }
}

function parseVariants(jsonText: string): string[] {
  try {
    const root = JSON.parse(jsonText) as unknown
    if (root === null || typeof root !== "object" || Array.isArray(root)) return []
    const raw = (root as Record<string, unknown>)["variants"]
    if (raw === null || raw === undefined) return []
    if (typeof raw !== "object" || Array.isArray(raw)) return []
    return Object.keys(raw as Record<string, unknown>)
  } catch {
    return []
  }
}

function collectBalancedJson(lines: string[], startIndex: number): string | null {
  let depth = 0
  let inString = false
  let escape = false
  const parts: string[] = []
  for (let i = startIndex; i < lines.length; i += 1) {
    const line = lines[i] ?? ""
    parts.push(line)
    for (let j = 0; j < line.length; j += 1) {
      const ch = line[j]
      if (escape) {
        escape = false
        continue
      }
      if (inString) {
        if (ch === "\\") escape = true
        else if (ch === '"') inString = false
        continue
      }
      if (ch === '"') inString = true
      else if (ch === "{") depth += 1
      else if (ch === "}") {
        depth -= 1
        if (depth === 0) {
          return parts.join("\n")
        }
      }
    }
  }
  return null
}

function countConsumedLines(jsonText: string): number {
  if (jsonText.length === 0) return 0
  return jsonText.split(/\r?\n/).length
}

// opencode streams its models output asynchronously, and the async execFile
// callback can fire (process 'close') while stdout pipe data is still buffered
// — observed truncating 49KB output to 32KB and silently dropping providers.
// spawnSync drains all pipes before returning, so it captures the full output.
function execFileText(command: string, args: string[], signal: AbortSignal): Promise<string> {
  assertExternalProcessAllowed("runtime/opencode-models.execFileText")
  const result = spawnSync(command, args, { signal, timeout: 10_000, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 })
  if (result.error) throw result.error
  if (result.status !== 0) throw new Error(`${command} ${args.join(" ")} exited with ${result.status}`)
  return Promise.resolve(result.stdout)
}
