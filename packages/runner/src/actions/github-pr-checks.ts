export interface PrCheckEntry {
  name: string
  bucket: string
  state: string
}

type PrStatusCheckRollupParseResult =
  | { kind: "ok"; checks: PrCheckEntry[] }
  | { kind: "invalid"; message: string }

export function parsePrStatusCheckRollup(stdout: string): PrCheckEntry[] {
  const parsed = parsePrStatusCheckRollupResult(stdout)
  return parsed.kind === "ok" ? parsed.checks : []
}

export function parsePrStatusCheckRollupResult(stdout: string): PrStatusCheckRollupParseResult {
  const trimmed = stdout.trim()
  if (!trimmed) return { kind: "invalid", message: "gh pr view statusCheckRollup returned empty output" }
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return { kind: "invalid", message: "gh pr view statusCheckRollup returned unparseable JSON" }
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    return { kind: "invalid", message: "gh pr view statusCheckRollup returned unexpected JSON" }
  }
  const rollup = (parsed as Record<string, unknown>)["statusCheckRollup"]
  if (!Array.isArray(rollup)) {
    return { kind: "invalid", message: "gh pr view statusCheckRollup did not include a statusCheckRollup array" }
  }
  const out: PrCheckEntry[] = []
  for (const item of rollup) {
    if (!item || typeof item !== "object" || Array.isArray(item)) continue
    const obj = item as Record<string, unknown>
    const name = typeof obj["name"] === "string"
      ? (obj["name"] as string)
      : typeof obj["context"] === "string"
        ? (obj["context"] as string)
        : ""
    const status = typeof obj["status"] === "string" ? (obj["status"] as string) : ""
    const rawState = typeof obj["state"] === "string" ? (obj["state"] as string) : ""
    const conclusion = typeof obj["conclusion"] === "string" ? (obj["conclusion"] as string) : ""
    const state = conclusion || rawState || status
    const bucket = classifyRollupBucket(status || rawState, conclusion)
    out.push({ name, bucket, state })
  }
  return { kind: "ok", checks: out }
}

function classifyRollupBucket(status: string, conclusion: string): string {
  const normalizedStatus = status.toUpperCase()
  const normalizedConclusion = conclusion.toUpperCase()
  if (normalizedConclusion === "SUCCESS") return "pass"
  if (normalizedConclusion === "SKIPPED" || normalizedConclusion === "NEUTRAL") return "skip"
  if (normalizedConclusion === "FAILURE" || normalizedConclusion === "ERROR" || normalizedConclusion === "CANCELLED" || normalizedConclusion === "ACTION_REQUIRED") return "fail"
  if (normalizedStatus === "SUCCESS") return "pass"
  if (normalizedStatus === "SKIPPED" || normalizedStatus === "NEUTRAL") return "skip"
  if (normalizedStatus === "FAILURE" || normalizedStatus === "ERROR" || normalizedStatus === "CANCELLED" || normalizedStatus === "ACTION_REQUIRED") return "fail"
  return "pending"
}

type PrChecksClassification =
  | { kind: "pending" }
  | { kind: "passed" }
  | { kind: "failed"; message: string }

export function classifyPrChecks(entries: PrCheckEntry[]): PrChecksClassification {
  if (entries.length === 0) return { kind: "pending" }
  const failed: string[] = []
  for (const entry of entries) {
    const bucket = (entry.bucket ?? "").toLowerCase()
    if (bucket === "pending" || bucket === "") {
      return { kind: "pending" }
    }
    if (bucket === "fail") {
      failed.push(formatFailedCheck(entry))
    }
  }
  if (failed.length > 0) {
    return { kind: "failed", message: failed.join("; ") }
  }
  return { kind: "passed" }
}

function formatFailedCheck(entry: PrCheckEntry): string {
  const label = entry.name || "unknown check"
  const bucket = entry.bucket || "FAIL"
  const state = entry.state && entry.state !== bucket ? ` (state=${entry.state})` : ""
  return `${label} [bucket=${bucket}]${state}`
}
