import type { WorkItemOrigin } from '../../../entities/issue'

export function classifyResult(result?: string): 'PASS' | 'FAIL' | 'UNKNOWN' {
  if (!result) return 'UNKNOWN'
  const upper = result.toUpperCase()
  if (upper === 'PASS') return 'PASS'
  if (upper === 'FAIL') return 'FAIL'
  return 'UNKNOWN'
}

export function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  const m = Math.floor(ms / 60000)
  const s = Math.floor((ms % 60000) / 1000)
  return `${m}m ${s}s`
}

export function formatClock(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

export function formatOriginLabel(origin?: WorkItemOrigin | null): string | null {
  if (!origin) return null
  const source = origin.source === 'builtin' ? 'built-in' : origin.source
  return `${source}:${origin.uses.replace(/^mohist\//, '')}`
}

export function formatOriginTitle(origin?: WorkItemOrigin | null): string | undefined {
  if (!origin) return undefined
  return `${origin.source} workflow item using ${origin.uses}`
}