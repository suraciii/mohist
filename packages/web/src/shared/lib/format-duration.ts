export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null || Number.isNaN(seconds)) return ''

  const totalSeconds = Math.max(0, seconds)
  const days = totalSeconds / 86_400
  const hours = totalSeconds / 3_600
  const minutes = totalSeconds / 60

  if (days >= 1) {
    return `${compactDurationValue(days, days >= 10 ? 0 : 1)}d`
  }
  if (hours >= 1) {
    return `${compactDurationValue(hours, hours >= 10 ? 0 : 1)}h`
  }
  if (minutes >= 1) {
    return `${Math.round(minutes)}m`
  }
  return '<1m'
}

function compactDurationValue(value: number, digits: number): string {
  return value.toFixed(digits).replace(/\.0$/, '')
}
