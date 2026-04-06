import { useState, useEffect, useRef, useCallback } from 'react'
import fuzzysort from 'fuzzysort'
import { Dialog } from './Dialog'
import { api } from '../lib/api'
import { useProject } from '../context/ProjectContext'
import type { DirEntry } from '../lib/types'

interface Props {
  open: boolean
  onClose: () => void
  onSelect: (path: string) => void
}

function expandTilde(input: string, home: string): string {
  if (input === '~') return home
  if (input.startsWith('~/')) return home + input.slice(1)
  return input
}

function collapseHome(absPath: string, home: string): string {
  if (absPath === home) return '~'
  if (absPath.startsWith(home + '/')) return '~' + absPath.slice(home.length)
  return absPath
}

function isPathMode(input: string): boolean {
  return input.startsWith('~') || input.startsWith('/')
}

function parsePathInput(input: string, home: string): { parent: string; fragment: string } {
  const expanded = expandTilde(input, home)
  const hasTrailingSlash = input.endsWith('/')

  if (hasTrailingSlash || expanded === home) {
    return { parent: expanded.replace(/\/$/, '') || '/', fragment: '' }
  }

  const lastSlash = expanded.lastIndexOf('/')
  if (lastSlash === -1) {
    return { parent: home, fragment: expanded }
  }
  const parent = expanded.slice(0, lastSlash) || '/'
  const fragment = expanded.slice(lastSlash + 1)
  return { parent, fragment }
}

function FolderIcon() {
  return (
    <svg className="h-4 w-4 text-gray-400 shrink-0" viewBox="0 0 20 20" fill="currentColor">
      <path d="M3.75 3A1.75 1.75 0 002 4.75v3.26a3.235 3.235 0 011.75-.51h12.5c.644 0 1.245.188 1.75.51V6.75A1.75 1.75 0 0016.25 5h-4.836a.25.25 0 01-.177-.073L9.823 3.513A1.75 1.75 0 008.586 3H3.75z" />
      <path d="M3.75 9A1.75 1.75 0 002 10.75v4.5c0 .966.784 1.75 1.75 1.75h12.5A1.75 1.75 0 0018 15.25v-4.5A1.75 1.75 0 0016.25 9H3.75z" />
    </svg>
  )
}

export function DialogSelectDirectory({ open, onClose, onSelect }: Props) {
  const { projects } = useProject()
  const inputRef = useRef<HTMLInputElement>(null)
  const [inputValue, setInputValue] = useState('')
  const [home, setHome] = useState('')
  const [results, setResults] = useState<DirEntry[]>([])
  const [loading, setLoading] = useState(false)
  const [selectedIndex, setSelectedIndex] = useState(0)
  const debounceRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  const recentProjects = projects.slice(0, 5)

  useEffect(() => {
    if (!open) {
      setInputValue('')
      setResults([])
      setSelectedIndex(0)
      return
    }
    if (!home) {
      api.getHomeDir().then(h => setHome(h)).catch(() => {})
    }
    setTimeout(() => inputRef.current?.focus(), 50)
  }, [open, home])

  const fetchPathResults = useCallback(async (input: string) => {
    if (!home || !input.trim()) {
      setResults([])
      return
    }

    if (isPathMode(input)) {
      const { parent, fragment } = parsePathInput(input, home)
      setLoading(true)
      try {
        const entries = await api.listDirectories(parent)
        const filtered = fragment
          ? fuzzysort
              .go(fragment, entries, { key: 'name', threshold: -1000 })
              .map(r => r.obj)
          : entries
        setResults(filtered)
        setSelectedIndex(0)
      } catch {
        setResults([])
      } finally {
        setLoading(false)
      }
    } else {
      setLoading(true)
      try {
        const entries = await api.searchDirectories(input)
        const ranked = fuzzysort
          .go(input, entries, { key: 'name' })
          .map(r => r.obj)
        setResults(ranked)
        setSelectedIndex(0)
      } catch {
        setResults([])
      } finally {
        setLoading(false)
      }
    }
  }, [home])

  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current)
    if (!open || !inputValue.trim()) {
      setResults([])
      return
    }
    debounceRef.current = setTimeout(() => fetchPathResults(inputValue), 200)
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current)
    }
  }, [inputValue, open, fetchPathResults])

  const allItems = recentProjects.length > 0 && !inputValue.trim()
    ? recentProjects.map(p => ({ name: p.name, absolute: p.path } as DirEntry))
    : results

  const maxIndex = Math.max(0, allItems.length - 1)

  const handleKeyDown = useCallback(
    async (e: React.KeyboardEvent) => {
      if (e.key === 'Tab' && home) {
        e.preventDefault()
        if (!isPathMode(inputValue)) return

        const { parent, fragment } = parsePathInput(inputValue, home)
        if (!fragment) return

        try {
          const entries = await api.listDirectories(parent)
          const matches = entries.filter(d => d.name.startsWith(fragment))
          if (matches.length === 1) {
            const collapsed = collapseHome(matches[0].absolute, home)
            setInputValue(collapsed + '/')
          } else if (matches.length > 1) {
            let common = fragment
            while (common.length < matches[0].name.length) {
              const next = common + matches[0].name[common.length]
              if (matches.every(m => m.name.startsWith(next))) {
                common = next
              } else break
            }
            const parentCollapsed = collapseHome(parent, home)
            setInputValue(parentCollapsed + '/' + common)
          }
        } catch {
          // ignore
        }
      } else if (e.key === 'ArrowDown') {
        e.preventDefault()
        setSelectedIndex(i => Math.min(i + 1, maxIndex))
      } else if (e.key === 'ArrowUp') {
        e.preventDefault()
        setSelectedIndex(i => Math.max(i - 1, 0))
      } else if (e.key === 'Enter') {
        e.preventDefault()
        if (allItems[selectedIndex]) {
          onSelect(allItems[selectedIndex].absolute)
        }
      }
    },
    [inputValue, home, allItems, maxIndex, selectedIndex, onSelect],
  )

  const displayPath = (abs: string) => (home ? collapseHome(abs, home) : abs)

  return (
    <Dialog open={open} onClose={onClose} title="Select Project Directory">
      <div className="space-y-3">
        <div className="relative">
          <div className="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400">
            <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
              <path
                fillRule="evenodd"
                d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
                clipRule="evenodd"
              />
            </svg>
          </div>
          <input
            ref={inputRef}
            type="text"
            value={inputValue}
            onChange={e => setInputValue(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Search or enter path..."
            className="w-full rounded-md border border-gray-300 pl-9 pr-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          />
        </div>

        <div className="max-h-72 overflow-y-auto rounded-md border border-gray-200">
          {recentProjects.length > 0 && !inputValue.trim() && (
            <div>
              <div className="px-3 py-1.5 text-xs font-medium text-gray-400 uppercase tracking-wider bg-gray-50 border-b border-gray-200">
                Recent Projects
              </div>
              {recentProjects.map(p => (
                <button
                  key={p.id}
                  onClick={() => onSelect(p.path)}
                  onMouseEnter={() => setSelectedIndex(recentProjects.indexOf(p))}
                  className={`w-full flex items-center gap-2 px-3 py-2 text-sm transition-colors border-b border-gray-100 last:border-0 ${
                    selectedIndex === recentProjects.indexOf(p)
                      ? 'bg-blue-50 text-blue-700'
                      : 'text-gray-700 hover:bg-gray-50'
                  }`}
                >
                  <FolderIcon />
                  <span className="truncate">{displayPath(p.path)}</span>
                </button>
              ))}
            </div>
          )}

          {loading && (
            <div className="px-3 py-6 text-center text-sm text-gray-400">
              Searching...
            </div>
          )}

          {!loading && results.length > 0 && (
            <div>
              {inputValue.trim() && (
                <div className="px-3 py-1.5 text-xs font-medium text-gray-400 uppercase tracking-wider bg-gray-50 border-b border-gray-200">
                  Directories
                </div>
              )}
              {results.map((entry, i) => (
                <button
                  key={entry.absolute}
                  onClick={() => onSelect(entry.absolute)}
                  onMouseEnter={() => setSelectedIndex(i)}
                  className={`w-full flex items-center gap-2 px-3 py-2 text-sm transition-colors border-b border-gray-100 last:border-0 ${
                    i === selectedIndex
                      ? 'bg-blue-50 text-blue-700'
                      : 'text-gray-700 hover:bg-gray-50'
                  }`}
                >
                  <FolderIcon />
                  <span className="truncate">{displayPath(entry.absolute)}</span>
                </button>
              ))}
            </div>
          )}

          {!loading && inputValue.trim() && results.length === 0 && (
            <div className="px-3 py-6 text-center text-sm text-gray-400">
              No directories found
            </div>
          )}

          {!inputValue.trim() && recentProjects.length === 0 && !loading && (
            <div className="px-3 py-6 text-center text-sm text-gray-400">
              Type to search directories or enter a path
            </div>
          )}
        </div>

        <div className="flex justify-end gap-2 pt-1">
          <button
            onClick={onClose}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={() => {
              if (allItems[selectedIndex]) onSelect(allItems[selectedIndex].absolute)
            }}
            disabled={!allItems[selectedIndex]}
            className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
          >
            Select
          </button>
        </div>
      </div>
    </Dialog>
  )
}
