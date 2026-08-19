import { closeSync, fstatSync, openSync, readSync, statSync } from 'node:fs'

const SETTLE_POLL_MS = 5_000
const SETTLE_POLLS_REQUIRED = 3

/** Minimal timer surface; PiClock satisfies this structurally. */
interface SettleClock {
  setTimeout(handler: () => void, ms: number): unknown
  clearTimeout(handle: unknown): void
}

interface SettleGuardDeps {
  clock: SettleClock
  sessionFile: string
  messages: () => readonly { role?: string; stopReason?: string }[]
  initialMessageCount: number
  isSettled: () => boolean
  onSettle: (fileTerminal: string | null) => void
}

/**
 * Watches a pi turn that may never return from `prompt()`. The guard polls
 * two terminal-state sources and settles after a consecutive-poll streak
 * from either:
 *
 * 1. In-memory agent state — a terminal assistant message produced after
 *    this turn started. The streak (rather than a wall-clock comparison)
 *    keeps the guard independent of the injected clock's now().
 * 2. The persisted session file — the ground truth reattach trusts. Agent
 *    state can diverge from it (overflow recovery removes the last
 *    assistant message from memory while the file keeps it, and the
 *    follow-up continue can hang before any event reaches memory).
 */
export function startSettleGuard(deps: SettleGuardDeps): () => void {
  let settleTimer: unknown = null
  let terminalStreak = 0
  let fileQuietTerminalStreak = 0
  // A reused session file already ends in the previous turn's terminal
  // message when this turn starts; only a file that has grown since then
  // counts as evidence for THIS turn.
  const initialFileSize = fileSizeOf(deps.sessionFile)
  const stop = () => {
    if (settleTimer !== null) {
      deps.clock.clearTimeout(settleTimer)
      settleTimer = null
    }
  }
  const poll = () => {
    if (deps.isSettled()) return
    const messages = deps.messages()
    // Only a message produced after this turn started counts; a terminal
    // assistant message from an earlier turn on a reused session must not
    // trigger a settle while the new turn is still in flight.
    const terminal = messages.length > deps.initialMessageCount && lastMessageTerminal(messages)
    terminalStreak = terminal ? terminalStreak + 1 : 0
    if (terminalStreak >= SETTLE_POLLS_REQUIRED) {
      stop()
      deps.onSettle(null)
      return
    }
    const fileTerminal = fileSizeOf(deps.sessionFile) > initialFileSize ? readTerminalFromFile(deps.sessionFile) : null
    fileQuietTerminalStreak = fileTerminal !== null ? fileQuietTerminalStreak + 1 : 0
    if (fileQuietTerminalStreak >= SETTLE_POLLS_REQUIRED) {
      stop()
      deps.onSettle(fileTerminal)
      return
    }
    settleTimer = deps.clock.setTimeout(poll, SETTLE_POLL_MS)
  }
  settleTimer = deps.clock.setTimeout(poll, SETTLE_POLL_MS)
  return stop
}

function lastMessageTerminal(messages: readonly { role?: string; stopReason?: string }[]): boolean {
  const item = [...messages].reverse().find((entry) => entry.role === 'assistant')
  return item?.stopReason === 'stop' || item?.stopReason === 'error' || item?.stopReason === 'aborted'
}

// The persisted session file is the ground truth reattach trusts; agent state
// can diverge from it (overflow recovery removes the last assistant message
// from memory while the file keeps it). Reads the tail of the jsonl file and
// reports the stopReason of the last message entry when it is a terminal
// assistant message.
function readTerminalFromFile(filePath: string): string | null {
  try {
    const fd = openSync(filePath, 'r')
    try {
      const size = fstatSync(fd).size
      const start = Math.max(0, size - 65_536)
      const length = size - start
      if (length <= 0) return null
      const buffer = Buffer.alloc(length)
      readSync(fd, buffer, 0, length, start)
      const lines = buffer.toString('utf8').split('\n')
      for (let i = lines.length - 1; i >= 0; i--) {
        const line = lines[i].trim()
        if (!line) continue
        try {
          const entry: unknown = JSON.parse(line)
          if (!(entry instanceof Object) || (entry as { type?: string }).type !== 'message') continue
          const message = (entry as { message?: { role?: string; stopReason?: string } }).message
          if (message?.role !== 'assistant') return null
          const stop = message.stopReason
          return stop === 'stop' || stop === 'error' || stop === 'aborted' ? stop : null
        } catch {
          continue
        }
      }
      return null
    } finally {
      closeSync(fd)
    }
  } catch {
    return null
  }
}

function fileSizeOf(filePath: string): number {
  try {
    return statSync(filePath).size
  } catch {
    return -1
  }
}
