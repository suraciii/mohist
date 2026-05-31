import { useState, useEffect, useRef, useCallback } from 'react'
import fuzzysort from 'fuzzysort'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { getHomeDir, listDirectories, searchDirectories, type DirEntry, type Project } from '../../../entities/project'

interface Props {
  open: boolean
  recentProjects?: Project[]
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
    <svg className="h-4 w-4 text-muted-foreground shrink-0" viewBox="0 0 20 20" fill="currentColor">
      <path d="M3.75 3A1.75 1.75 0 002 4.75v3.26a3.235 3.235 0 011.75-.51h12.5c.644 0 1.245.188 1.75.51V6.75A1.75 1.75 0 0016.25 5h-4.836a.25.25 0 01-.177-.073L9.823 3.513A1.75 1.75 0 008.586 3H3.75z" />
      <path d="M3.75 9A1.75 1.75 0 002 10.75v4.5c0 .966.784 1.75 1.75 1.75h12.5A1.75 1.75 0 0018 15.25v-4.5A1.75 1.75 0 0016.25 9H3.75z" />
    </svg>
  )
}

export function DialogSelectDirectory({ open, recentProjects = [], onClose, onSelect }: Props) {
  const inputRef = useRef<HTMLInputElement>(null)
  const [inputValue, setInputValue] = useState('')
  const [home, setHome] = useState('')
  const [results, setResults] = useState<DirEntry[]>([])
  const [loading, setLoading] = useState(false)
  const [selectedIndex, setSelectedIndex] = useState(0)
  const debounceRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  const displayedRecentProjects = recentProjects.slice(0, 5)

  useEffect(() => {
    if (!open) {
      setInputValue('')
      setResults([])
      setSelectedIndex(0)
      return
    }
    if (!home) {
      getHomeDir().then(h => setHome(h)).catch(() => {})
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
        const entries = await listDirectories(parent)
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
        const entries = await searchDirectories(input)
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

  const allItems = displayedRecentProjects.length > 0 && !inputValue.trim()
    ? displayedRecentProjects.map(p => ({ name: p.name, absolute: p.path } as DirEntry))
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
          const entries = await listDirectories(parent)
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
          setResults([])
          setSelectedIndex(0)
        } catch {
          // ignore autocomplete errors
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
    <Dialog open={open} onOpenChange={(nextOpen) => !nextOpen && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Select Project Directory</DialogTitle>
        </DialogHeader>
        <div className="space-y-3">
          <div className="relative">
            <div className="absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground">
              <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
                <path
                  fillRule="evenodd"
                  d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
                  clipRule="evenodd"
                />
              </svg>
            </div>
            <Input
              ref={inputRef}
              value={inputValue}
              onChange={e => setInputValue(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Search or enter path..."
              className="pl-9"
            />
          </div>

          <div className="max-h-72 overflow-y-auto rounded-md border">
            {displayedRecentProjects.length > 0 && !inputValue.trim() && (
              <div>
                <div className="px-3 py-1.5 text-xs font-medium text-muted-foreground uppercase tracking-wider bg-muted border-b">
                  Recent Projects
                </div>
                {displayedRecentProjects.map(p => (
                  <Button
                    key={p.id}
                    variant="ghost"
                    onClick={() => onSelect(p.path)}
                    onMouseEnter={() => setSelectedIndex(displayedRecentProjects.indexOf(p))}
                    className={`w-full justify-start rounded-none border-b last:border-0 ${
                      selectedIndex === displayedRecentProjects.indexOf(p)
                        ? 'bg-blue-50 text-blue-700'
                        : 'text-foreground/80 hover:bg-muted'
                    }`}
                  >
                    <FolderIcon />
                    <span className="truncate">{displayPath(p.path)}</span>
                  </Button>
                ))}
              </div>
            )}

            {loading && (
              <div className="px-3 py-6 text-center text-sm text-muted-foreground">
                Searching...
              </div>
            )}

            {!loading && results.length > 0 && (
              <div>
                {inputValue.trim() && (
                  <div className="px-3 py-1.5 text-xs font-medium text-muted-foreground uppercase tracking-wider bg-muted border-b">
                    Directories
                  </div>
                )}
                {results.map((entry, i) => (
                  <Button
                    key={entry.absolute}
                    variant="ghost"
                    onClick={() => onSelect(entry.absolute)}
                    onMouseEnter={() => setSelectedIndex(i)}
                    className={`w-full justify-start rounded-none border-b last:border-0 ${
                      i === selectedIndex
                        ? 'bg-blue-50 text-blue-700'
                        : 'text-foreground/80 hover:bg-muted'
                    }`}
                  >
                    <FolderIcon />
                    <span className="truncate">{displayPath(entry.absolute)}</span>
                  </Button>
                ))}
              </div>
            )}

            {!loading && inputValue.trim() && results.length === 0 && (
              <div className="px-3 py-6 text-center text-sm text-muted-foreground">
                No directories found
              </div>
            )}

            {!inputValue.trim() && displayedRecentProjects.length === 0 && !loading && (
              <div className="px-3 py-6 text-center text-sm text-muted-foreground">
                Type to search directories or enter a path
              </div>
            )}
          </div>

          <div className="flex justify-end gap-2 pt-1">
            <Button variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button
              onClick={() => {
                if (allItems[selectedIndex]) onSelect(allItems[selectedIndex].absolute)
              }}
              disabled={!allItems[selectedIndex]}
            >
              Select
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
