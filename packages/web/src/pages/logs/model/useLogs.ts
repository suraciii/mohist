import { useState, useEffect, useRef, useCallback } from 'react'
import { getLogTail, type LogEntry, type LogTailResult } from './api'

const MAX_ENTRIES = 2000
const POLL_INTERVAL = 3000

export interface UseLogsReturn {
  entries: LogEntry[]
  loading: boolean
  error: string | null
  refresh: () => void
  cursor: number | null
  nextCursor: number | null
  source: string | null
  unavailable: boolean
  expectedLocation: string | null
  reason: string | null
  truncated: boolean
  reset: boolean
}

export function useLogs(): UseLogsReturn {
  const [entries, setEntries] = useState<LogEntry[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [cursor, setCursor] = useState<number | null>(null)
  const [nextCursor, setNextCursor] = useState<number | null>(null)
  const [source, setSource] = useState<string | null>(null)
  const [unavailable, setUnavailable] = useState(false)
  const [expectedLocation, setExpectedLocation] = useState<string | null>(null)
  const [reason, setReason] = useState<string | null>(null)
  const [truncated, setTruncated] = useState(false)
  const [reset, setReset] = useState(false)

  const cursorRef = useRef<number | null>(null)
  const visibleRef = useRef(true)
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const fetchingRef = useRef(false)

  const applyResult = useCallback((result: LogTailResult) => {
    const effectiveReset = result.reset || result.unavailable

    if (effectiveReset) {
      setEntries(result.unavailable ? [] : result.lines)
    } else {
      setEntries((prev) => {
        const next = [...prev, ...result.lines]
        return next.length > MAX_ENTRIES ? next.slice(next.length - MAX_ENTRIES) : next
      })
    }
    cursorRef.current = result.unavailable ? null : result.nextCursor
    setCursor(result.unavailable ? null : result.cursor)
    setNextCursor(result.unavailable ? null : result.nextCursor)
    setSource(result.unavailable ? null : result.source)
    setUnavailable(result.unavailable)
    setExpectedLocation(result.expectedLocation)
    setReason(result.reason)
    setTruncated(result.unavailable ? false : result.truncated)
    setReset(effectiveReset)
  }, [])

  const fetchLogs = useCallback(async () => {
    if (fetchingRef.current) return
    fetchingRef.current = true
    try {
      const result = cursorRef.current == null
        ? await getLogTail()
        : await getLogTail(cursorRef.current)

      applyResult(result)
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch logs')
    } finally {
      setLoading(false)
      fetchingRef.current = false
    }
  }, [applyResult])

  const refresh = useCallback(() => {
    fetchLogs()
  }, [fetchLogs])

  useEffect(() => {
    fetchLogs()
  }, [fetchLogs])

  useEffect(() => {
    const tick = () => {
      if (visibleRef.current) {
        fetchLogs()
      }
    }
    intervalRef.current = setInterval(tick, POLL_INTERVAL)
    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current)
        intervalRef.current = null
      }
    }
  }, [fetchLogs])

  useEffect(() => {
    const handleVisibility = () => {
      const visible = document.visibilityState === 'visible'
      visibleRef.current = visible
      if (visible) {
        fetchLogs()
      }
    }
    document.addEventListener('visibilitychange', handleVisibility)
    return () => {
      document.removeEventListener('visibilitychange', handleVisibility)
    }
  }, [fetchLogs])

  return {
    entries,
    loading,
    error,
    refresh,
    cursor,
    nextCursor,
    source,
    unavailable,
    expectedLocation,
    reason,
    truncated,
    reset,
  }
}
