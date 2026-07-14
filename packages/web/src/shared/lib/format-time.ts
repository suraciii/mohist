export function formatTime(iso: string): string {
  return new Date(iso).toLocaleString()
}

export function formatTimeAgo(date: Date): string {
  const now = new Date()
  const diffMs = now.getTime() - date.getTime()
  const diffMin = Math.floor(diffMs / 60000)
  const diffHr = Math.floor(diffMin / 60)
  const diffDay = Math.floor(diffHr / 24)

  if (diffMin < 1) return 'just now'
  if (diffMin < 60) return `${diffMin}m ago`
  if (diffHr < 24) return `${diffHr}h ago`
  if (diffDay < 30) return `${diffDay}d ago`
  return date.toLocaleDateString()
}

export function formatElapsedTimeAgo(isoString: string, now = Date.now()): string {
  const timestamp = Date.parse(isoString)
  if (!Number.isFinite(timestamp)) return 'unknown'

  const minutes = Math.floor(Math.max(0, now - timestamp) / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes}m ago`

  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

export function formatLogTime(time: string | null): string {
  if (!time) return '--:--:--'
  try {
    const d = new Date(time)
    return d.toLocaleTimeString('en-US', { hour12: false })
  } catch {
    return time
  }
}
