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

export function formatLogTime(time: string | null): string {
  if (!time) return '--:--:--'
  try {
    const d = new Date(time)
    return d.toLocaleTimeString('en-US', { hour12: false })
  } catch {
    return time
  }
}
