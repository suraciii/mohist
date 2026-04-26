export function formatRelativeTime(date: string | null | undefined): string {
  if (!date) return ''

  const now = new Date().getTime()
  const then = new Date(date).getTime()
  const diffMs = now - then

  if (diffMs < 0) return ''

  const seconds = Math.floor(diffMs / 1000)
  const minutes = Math.floor(seconds / 60)
  const hours = Math.floor(minutes / 60)
  const days = Math.floor(hours / 24)

  if (days > 0) return `${days}d ago`
  if (hours > 0) return `${hours}h ago`
  if (minutes > 0) return `${minutes}m ago`
  return 'just now'
}
