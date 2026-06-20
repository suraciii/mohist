import { execFile } from "node:child_process"

export interface DiscoveredOpencodeModels {
  models: string[]
  variants: Record<string, string[]>
}

const CACHE_TTL_MS = 30 * 60 * 1000

let cached: { result: DiscoveredOpencodeModels; fetchedAt: number } | null = null

export async function discoverOpencodeModels(signal: AbortSignal): Promise<DiscoveredOpencodeModels> {
  const now = Date.now()
  if (cached !== null && now - cached.fetchedAt < CACHE_TTL_MS) {
    return cached.result
  }

  const command = process.env.MOHIST_AGENT_MODELS_COMMAND ?? process.env.MOHIST_AGENT_COMMAND ?? "opencode"

  let result: DiscoveredOpencodeModels
  try {
    const stdout = await execFileText(command, ["models", "--verbose"], signal)
    result = parseOpencodeModelsVerbose(stdout)
  } catch (error) {
    console.error("failed to discover opencode models", error)
    result = { models: [], variants: {} }
  }

  if (result.models.length > 0) {
    cached = { result, fetchedAt: now }
  }
  return result
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

export function clearOpencodeModelsCacheForTesting(): void {
  cached = null
}

function execFileText(command: string, args: string[], signal: AbortSignal): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = execFile(command, args, { signal, timeout: 10_000 }, (error, stdout) => {
      if (error) reject(error)
      else resolve(stdout)
    })
    child.unref?.()
  })
}
