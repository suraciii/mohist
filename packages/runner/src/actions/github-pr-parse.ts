export function parsePrList(stdout: string): { number: number; url: string }[] {
  const trimmed = stdout.trim()
  if (!trimmed) return []
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return []
  }
  if (!Array.isArray(parsed)) return []
  const out: { number: number; url: string }[] = []
  for (const item of parsed) {
    if (!item || typeof item !== "object" || Array.isArray(item)) continue
    const number = (item as Record<string, unknown>)["number"]
    const url = (item as Record<string, unknown>)["url"]
    if (typeof number === "number" && typeof url === "string") {
      out.push({ number, url })
    }
  }
  return out
}

export function parsePrListWithDraft(stdout: string): { number: number; url: string; isDraft: boolean }[] {
  const trimmed = stdout.trim()
  if (!trimmed) return []
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return []
  }
  if (!Array.isArray(parsed)) return []
  const out: { number: number; url: string; isDraft: boolean }[] = []
  for (const item of parsed) {
    if (!item || typeof item !== "object" || Array.isArray(item)) continue
    const obj = item as Record<string, unknown>
    const number = obj["number"]
    const url = obj["url"]
    const draft = obj["isDraft"]
    if (typeof number === "number" && typeof url === "string") {
      out.push({ number, url, isDraft: draft === true })
    }
  }
  return out
}

interface PrViewState {
  state?: string
  url?: string
  mergeCommit?: { oid?: string } | null
  isDraft?: boolean
  mergeStateStatus?: string
}

export function parsePrView(stdout: string): PrViewState | null {
  return parsePrViewInternal(stdout, false)
}

export function parsePrViewWithDraft(stdout: string): PrViewState | null {
  return parsePrViewInternal(stdout, true)
}

function parsePrViewInternal(stdout: string, includeDraft: boolean): PrViewState | null {
  const trimmed = stdout.trim()
  if (!trimmed) return null
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return null
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null
  const obj = parsed as Record<string, unknown>
  const state = typeof obj["state"] === "string" ? (obj["state"] as string) : undefined
  const url = typeof obj["url"] === "string" ? (obj["url"] as string) : undefined
  const rawMergeCommit = obj["mergeCommit"]
  const mergeCommit = rawMergeCommit && typeof rawMergeCommit === "object" && !Array.isArray(rawMergeCommit)
    ? { oid: typeof (rawMergeCommit as Record<string, unknown>)["oid"] === "string" ? ((rawMergeCommit as Record<string, unknown>)["oid"] as string) : undefined }
    : null
  const result: PrViewState = { state, url, mergeCommit, mergeStateStatus: typeof obj["mergeStateStatus"] === "string" ? (obj["mergeStateStatus"] as string) : undefined }
  if (includeDraft) result.isDraft = obj["isDraft"] === true
  return result
}

export function extractPrNumberFromUrl(url: string): number | null {
  const match = url.match(/\/pull\/(\d+)/)
  if (!match || !match[1]) return null
  const n = Number(match[1])
  return Number.isFinite(n) ? n : null
}

export function combinedGhOutput(result: { stdout: string; stderr: string }): string {
  return [result.stdout.trim(), result.stderr.trim()].filter(Boolean).join("\n")
}

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
