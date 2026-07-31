import type { DirectoryReclaimResult } from "./directory-instance.js"

export function formatDirectoryReclaimSummary(result: DirectoryReclaimResult): string {
  const counts = new Map<string, number>()
  for (const diagnostic of result.diagnostics) {
    counts.set(diagnostic.code, (counts.get(diagnostic.code) ?? 0) + 1)
  }

  const sortedCounts = [...counts.entries()].sort(([left], [right]) => left.localeCompare(right))
  const visibleCounts = sortedCounts.slice(0, 4)
  const diagnosticText = visibleCounts.length > 0
    ? visibleCounts.map(([code, count]) => `${code}:${count}`).join(",")
    : "none:0"

  return `workspace reclaim: tracked=${result.tracked} candidates=${result.candidates} disposed=${result.disposed} busy=${result.busy} failed=${result.failed} diagnostics=${diagnosticText} omitted=${Math.max(0, sortedCounts.length - visibleCounts.length)}`
}
