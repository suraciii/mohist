import { useState, useEffect, useRef, useCallback } from 'react'
import { api } from '../lib/api'
import type { LogTailResult } from '../lib/types'

export interface ParsedLogEntry {
  raw: string
  level: string | null
  time: string | null
  service: string | null
  message: string
}

export function parseLogLine(line: string): ParsedLogEntry {
  try {
    const obj = JSON.parse(line)
    return {
      raw: line,
      level: typeof obj.level === 'string' ? obj.level : null,
      time: typeof obj.time === 'string' ? obj.time : null,
      service: typeof obj.service === 'string' ? obj.service : null,
      message: typeof obj.message === 'string' ? obj.message : line,
    }
  } catch {
    return {
      raw: line,
      level: null,
      time: null,
      service: null,
      message: line,
    }
  }
}

const MAX_ENTRIES = 2000
const POLL_INTERVAL = 3000

interface UseLogsReturn {
  entries: ParsedLogEntry[]
  loading: boolean
  error: string | null
  refresh: () => void
  cursor: number
  truncated: boolean
  file: string | null
}

export function useLogs(): UseLogsReturn {
  const [entries, setEntries] = useState<ParsedLogEntry[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [cursor, setCursor] = useState<number>(0)
  const [truncated, setTruncated] = useState(false)
  const [file, setFile] = useState<string | null>(null)

  const cursorRef = useRef(0)
  const visibleRef = useRef(true)
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const fetchingRef = useRef(false)

  const fetchLogs = useCallback(async (useCursor?: number) => {
    if (fetchingRef.current) return
    fetchingRef.current = true
    try {
      const c = useCursor ?? cursorRef.current
      const result: LogTailResult = c === 0
        ? await api.getLogTail()
        : await api.getLogTail(c)

      const parsed: ParsedLogEntry[] = result.lines.map(parseLogLine)

      if (result.reset) {
        setEntries(parsed)
      } else {
        setEntries((prev: ParsedLogEntry[]) => {
          const next = [...prev, ...parsed]
          return next.length > MAX_ENTRIES ? next.slice(next.length - MAX_ENTRIES) : next
        })
      }

      cursorRef.current = result.cursor
      setCursor(result.cursor)
      setTruncated(result.truncated)
      setFile(result.file)
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to fetch logs')
    } finally {
      setLoading(false)
      fetchingRef.current = false
    }
  }, [])

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

  return { entries, loading, error, refresh, cursor, truncated, file }
}
