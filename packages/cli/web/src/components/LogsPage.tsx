import { useState, useRef, useEffect, useMemo, useCallback } from 'react'
import { useLogs } from '../hooks/useLogs'
import type { ParsedLogEntry } from '../hooks/useLogs'

const LEVEL_COLORS: Record<string, string> = {
  ERROR: 'text-red-600 bg-red-50',
  WARN: 'text-yellow-600 bg-yellow-50',
  INFO: 'text-blue-600 bg-blue-50',
  DEBUG: 'text-gray-500 bg-gray-100',
}

const LEVEL_CHIP_COLORS: Record<string, string> = {
  ERROR: 'bg-red-100 text-red-700 border-red-200',
  WARN: 'bg-yellow-100 text-yellow-700 border-yellow-200',
  INFO: 'bg-blue-100 text-blue-700 border-blue-200',
  DEBUG: 'bg-gray-100 text-gray-600 border-gray-200',
}

const ALL_LEVELS = ['DEBUG', 'INFO', 'WARN', 'ERROR'] as const
type LogLevel = (typeof ALL_LEVELS)[number]

function formatTime(time: string | null): string {
  if (!time) return '--:--:--'
  try {
    const d = new Date(time)
    return d.toLocaleTimeString('en-US', { hour12: false })
  } catch {
    return time
  }
}

function LogRow({ entry }: { entry: ParsedLogEntry }) {
  const levelColor = entry.level ? LEVEL_COLORS[entry.level] || 'text-gray-600 bg-gray-50' : 'text-gray-400 bg-gray-50'

  return (
    <div className="flex items-start gap-3 px-3 py-1 hover:bg-gray-50 text-xs font-mono border-b border-gray-100 last:border-b-0">
      <span className="text-gray-400 shrink-0 w-20 tabular-nums">{formatTime(entry.time)}</span>
      <span className={`shrink-0 px-1.5 py-0.5 rounded text-[10px] font-semibold uppercase leading-none ${levelColor}`}>
        {(entry.level || '????').padEnd(5)}
      </span>
      {entry.service && (
        <span className="shrink-0 text-purple-600 bg-purple-50 px-1.5 py-0.5 rounded text-[10px] leading-none">
          {entry.service}
        </span>
      )}
      <span className="text-gray-800 break-all min-w-0">{entry.message}</span>
    </div>
  )
}

export function LogsPage() {
  const { entries, loading, error, truncated, file } = useLogs()
  const [enabledLevels, setEnabledLevels] = useState<Set<LogLevel>>(new Set(ALL_LEVELS))
  const [searchQuery, setSearchQuery] = useState('')
  const [autoFollow, setAutoFollow] = useState(true)
  const [userPausedAutoFollow, setUserPausedAutoFollow] = useState(false)
  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const bottomRef = useRef<HTMLDivElement>(null)

  const toggleLevel = useCallback((level: LogLevel) => {
    setEnabledLevels((prev) => {
      const next = new Set(prev)
      if (next.has(level)) {
        next.delete(level)
      } else {
        next.add(level)
      }
      return next
    })
  }, [])

  const filtered = useMemo(() => {
    const q = searchQuery.toLowerCase().trim()
    return entries.filter((entry) => {
      if (entry.level && !enabledLevels.has(entry.level as LogLevel)) return false
      if (q) {
        const haystack = `${entry.message} ${entry.service || ''} ${entry.raw}`.toLowerCase()
        if (!haystack.includes(q)) return false
      }
      return true
    })
  }, [entries, enabledLevels, searchQuery])

  useEffect(() => {
    if (!autoFollow || userPausedAutoFollow) return
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [entries.length, autoFollow, userPausedAutoFollow])

  const handleScroll = useCallback(() => {
    const el = scrollContainerRef.current
    if (!el) return
    const distFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight
    if (distFromBottom > 10) {
      setUserPausedAutoFollow(true)
    } else {
      setUserPausedAutoFollow(false)
    }
  }, [])

  useEffect(() => {
    const el = scrollContainerRef.current
    if (!el) return
    el.addEventListener('scroll', handleScroll, { passive: true })
    return () => el.removeEventListener('scroll', handleScroll)
  }, [handleScroll])

  const handleExport = useCallback(() => {
    const text = filtered.map((e) => e.raw).join('\n')
    const blob = new Blob([text], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `mohist-logs-${new Date().toISOString().slice(0, 10)}.txt`
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    URL.revokeObjectURL(url)
  }, [filtered])

  return (
    <div className="flex-1 flex flex-col bg-gray-50 overflow-hidden">
      <div className="shrink-0 border-b border-gray-200 bg-white px-6 py-3">
        <div className="flex items-center justify-between mb-2">
          <h1 className="text-lg font-semibold text-gray-900">Logs</h1>
          <div className="flex items-center gap-2">
            <button
              onClick={() => setAutoFollow(!autoFollow)}
              className={`inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-xs font-medium transition-colors ${
                autoFollow
                  ? 'border-blue-300 bg-blue-50 text-blue-700'
                  : 'border-gray-300 bg-white text-gray-600 hover:bg-gray-50'
              }`}
            >
              <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M5.22 10.22a.75.75 0 001.06 0L10 6.56l3.72 3.66a.75.75 0 001.06-1.06l-4.25-4.18a.75.75 0 00-1.06 0L5.22 9.16a.75.75 0 000 1.06z" clipRule="evenodd" />
                <path fillRule="evenodd" d="M5.22 14.72a.75.75 0 001.06 0L10 11.06l3.72 3.66a.75.75 0 101.06-1.06l-4.25-4.18a.75.75 0 00-1.06 0L5.22 13.66a.75.75 0 000 1.06z" clipRule="evenodd" />
              </svg>
              Auto-follow
            </button>
            <button
              onClick={handleExport}
              disabled={filtered.length === 0}
              className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-2.5 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-40 disabled:cursor-not-allowed transition-colors shadow-sm"
            >
              <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
                <path d="M10.75 2.75a.75.75 0 00-1.5 0v8.614L6.295 8.235a.75.75 0 10-1.09 1.03l4.25 4.5a.75.75 0 001.09 0l4.25-4.5a.75.75 0 00-1.09-1.03l-2.955 3.129V2.75z" />
                <path d="M3.5 12.75a.75.75 0 00-1.5 0v2.5A2.75 2.75 0 004.75 18h10.5A2.75 2.75 0 0018 15.25v-2.5a.75.75 0 00-1.5 0v2.5c0 .69-.56 1.25-1.25 1.25H4.75c-.69 0-1.25-.56-1.25-1.25v-2.5z" />
              </svg>
              Export
            </button>
          </div>
        </div>

        <div className="flex items-center gap-3 flex-wrap">
          <div className="flex items-center gap-1.5">
            {ALL_LEVELS.map((level) => {
              const active = enabledLevels.has(level)
              return (
                <button
                  key={level}
                  onClick={() => toggleLevel(level)}
                  className={`inline-flex items-center rounded-full border px-2.5 py-0.5 text-xs font-semibold transition-colors ${
                    active
                      ? LEVEL_CHIP_COLORS[level]
                      : 'bg-white border-gray-200 text-gray-400 line-through'
                  }`}
                >
                  {level}
                </button>
              )
            })}
          </div>

          <div className="relative flex-1 min-w-[200px] max-w-xs">
            <svg
              className="absolute left-2 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-gray-400"
              viewBox="0 0 20 20"
              fill="currentColor"
            >
              <path
                fillRule="evenodd"
                d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
                clipRule="evenodd"
              />
            </svg>
            <input
              type="text"
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              placeholder="Search logs..."
              className="w-full rounded-md border border-gray-300 bg-white pl-7 pr-3 py-1 text-xs text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus:outline-none"
            />
          </div>
        </div>
      </div>

      {truncated && (
        <div className="shrink-0 bg-yellow-50 border-b border-yellow-200 px-6 py-1.5 text-xs text-yellow-700">
          Log output truncated; showing latest chunk
        </div>
      )}

      {file && (
        <div className="shrink-0 bg-gray-100 border-b border-gray-200 px-6 py-1 text-xs text-gray-500 font-mono">
          File: {file}
        </div>
      )}

      {loading && entries.length === 0 ? (
        <div className="flex-1 flex items-center justify-center">
          <div className="text-gray-400 text-sm">Loading logs...</div>
        </div>
      ) : error ? (
        <div className="flex-1 flex items-center justify-center">
          <div className="text-center">
            <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600 mb-2">{error}</div>
          </div>
        </div>
      ) : filtered.length === 0 ? (
        <div className="flex-1 flex items-center justify-center">
          <div className="text-gray-400 text-sm">
            {entries.length === 0 ? 'No logs available' : 'No matching logs'}
          </div>
        </div>
      ) : (
        <div ref={scrollContainerRef} className="flex-1 overflow-y-auto bg-white">
          {filtered.map((entry, i) => (
            <LogRow key={i} entry={entry} />
          ))}
          <div ref={bottomRef} />
        </div>
      )}
    </div>
  )
}
