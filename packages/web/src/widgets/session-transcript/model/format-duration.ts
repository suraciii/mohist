export function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '0s'
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  const totalSec = Math.floor(ms / 1000)
  const min = Math.floor(totalSec / 60)
  const sec = totalSec % 60
  if (min < 60) return `${min}m ${String(sec).padStart(2, '0')}s`
  const hr = Math.floor(min / 60)
  const remMin = min % 60
  return `${hr}h ${String(remMin).padStart(2, '0')}m`
}

function toMillis(value: string | null | undefined): number | null {
  if (!value) return null
  const t = new Date(value).getTime()
  return Number.isFinite(t) ? t : null
}

export function formatElapsed(
  startedAt: string | null | undefined,
  completedAt: string | null | undefined,
): string | null {
  const start = toMillis(startedAt)
  const end = toMillis(completedAt)
  if (start === null || end === null) return null
  const diff = end - start
  if (!Number.isFinite(diff) || diff < 0) return null
  return formatDuration(diff)
}

export function formatElapsedNow(
  startedAt: string | null | undefined,
  nowMs: number,
): string | null {
  const start = toMillis(startedAt)
  if (start === null) return null
  if (!Number.isFinite(nowMs)) return null
  const diff = nowMs - start
  if (!Number.isFinite(diff) || diff < 0) return null
  return formatDuration(diff)
}
