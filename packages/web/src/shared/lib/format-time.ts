export function formatTime(iso: string): string {
  return new Date(iso).toLocaleString()
}

export function formatTimeAgo(date: Date, now = Date.now()): string {
  const diffMs = now - date.getTime()
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

/**
 * Status kinds the time helper recognises. Mirrors the union used by the
 * session UI without dragging the session entity type into the shared layer
 * (FSD: `shared/lib` cannot depend on `entities/*`).
 */
export type SessionTimeStatusKind =
  | 'idle'
  | 'active'
  | 'unknown'
  | 'loading'
  | 'live'
  | 'finalizing'
  | 'probing'
  | 'recovering'
  | 'completed'
  | 'failed'
  | 'stale'

const TERMINAL_STATUS_KINDS: ReadonlySet<SessionTimeStatusKind> = new Set(['completed', 'failed', 'stale'])

const ABSOLUTE_FORMAT = new Intl.DateTimeFormat('en-US', {
  month: 'short',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
})

const ABSOLUTE_RELATIVE_THRESHOLD_MS = 60 * 60 * 1000

export interface FormatSessionTimeInput {
  /** The anchor timestamp being formatted. May be an ISO string, epoch ms, or Date. */
  date: string | number | Date
  /** Session status; controls terminal-vs-live branch. */
  statusKind: SessionTimeStatusKind
  /** Reference clock (epoch ms). The helper does not call `Date.now()` itself. */
  now: number
}

export interface FormatSessionTimeOutput {
  /** What to render inline. */
  primary: string
  /** What to expose via hover/focus tooltip (the complementary form). */
  secondary: string
}

/**
 * Status-aware absolute/relative time formatter for session views.
 *
 * Branches per `session-time-display/spec.md`:
 * - Terminal (`completed` / `failed` / `stale`) past the 1-hour threshold:
 *   primary = absolute, secondary = relative.
 * - Terminal within the 1-hour threshold: primary = relative, secondary = absolute.
 * - Non-terminal (`live` / `finalizing` / `probing`) at any threshold:
 *   primary = relative, secondary = absolute.
 *
 * The helper is pure: it never reads the system clock; all clock input flows
 * through the `now` argument so unit tests can vary the branch without
 * `vi.useFakeTimers`.
 */
export function formatSessionTime({ date, statusKind, now }: FormatSessionTimeInput): FormatSessionTimeOutput {
  const dateMs = toEpochMs(date)
  const absolute = ABSOLUTE_FORMAT.format(new Date(dateMs))
  const relative = formatRelativeForSessionTime(dateMs, now)

  const isTerminal = TERMINAL_STATUS_KINDS.has(statusKind)
  const pastThreshold = now - dateMs >= ABSOLUTE_RELATIVE_THRESHOLD_MS
  if (isTerminal && pastThreshold) return { primary: absolute, secondary: relative }
  return { primary: relative, secondary: absolute }
}

/** `Intl.DateTimeFormat` instance used to render the absolute arm. */
export const sessionTimeAbsoluteFormatter = ABSOLUTE_FORMAT

function toEpochMs(date: string | number | Date): number {
  if (date instanceof Date) return date.getTime()
  if (typeof date === 'number') return date
  const parsed = Date.parse(date)
  if (!Number.isFinite(parsed)) return NaN
  return parsed
}

function formatRelativeForSessionTime(dateMs: number, now: number): string {
  const diffMs = Math.max(0, now - dateMs)
  if (diffMs < 60_000) return 'just now'
  if (diffMs < 3_600_000) return `${Math.floor(diffMs / 60_000)}m ago`
  if (diffMs < 86_400_000) return `${Math.floor(diffMs / 3_600_000)}h ago`
  return `${Math.floor(diffMs / 86_400_000)}d ago`
}
