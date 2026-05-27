import { useState, useRef, useCallback, useEffect } from 'react'
import type { FileBlock } from '../model/diffModel'
import { classifyFile, DEFAULT_LARGE_DIFF_THRESHOLD } from '../model/diffModel'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

interface DiffSearchPaneProps {
  block: FileBlock | null
  threshold?: number
  activeHunkIndex?: number
  onActiveHunkChange?: (index: number) => void
  totalHunks?: number
  renderAnyway?: boolean
  onRenderAnyway?: () => void
}

export function DiffSearchPane({
  block,
  threshold = DEFAULT_LARGE_DIFF_THRESHOLD,
  activeHunkIndex = 0,
  onActiveHunkChange,
  totalHunks = 0,
  renderAnyway = false,
  onRenderAnyway,
}: DiffSearchPaneProps) {
  const [searchQuery, setSearchQuery] = useState('')
  const [searchMatches, setSearchMatches] = useState<number[]>([])
  const [currentMatchIndex, setCurrentMatchIndex] = useState(0)
  const containerRef = useRef<HTMLDivElement>(null)
  const highlightRefs = useRef<Map<number, HTMLTableRowElement>>(new Map())

  useEffect(() => {
    if (totalHunks > 0 && block && onActiveHunkChange) {
      const container = containerRef.current
      if (!container) return

      let hunkCount = 0
      let hunkElements: HTMLElement[] = []

      const rows = container.querySelectorAll('tr')
      rows.forEach((row) => {
        const cells = row.querySelectorAll('td')
        if (cells.length >= 3) {
          const content = cells[2].textContent ?? ''
          if (content.startsWith('@@')) {
            hunkElements.push(row as HTMLElement)
            hunkCount++
          }
        }
      })

      const targetHunk = Math.min(activeHunkIndex, hunkElements.length - 1)
      if (hunkElements[targetHunk]) {
        hunkElements[targetHunk].scrollIntoView({ behavior: 'smooth', block: 'start' })
      }
    }
  }, [activeHunkIndex, totalHunks, block, onActiveHunkChange])

  useEffect(() => {
    if (!searchQuery.trim()) {
      setSearchMatches([])
      return
    }

    if (!block) {
      setSearchMatches([])
      return
    }

    const matches: number[] = []
    const query = searchQuery.toLowerCase()
    block.lines.forEach((line, index) => {
      if (line.content.toLowerCase().includes(query)) {
        matches.push(index)
      }
    })
    setSearchMatches(matches)
    setCurrentMatchIndex(0)

    if (matches.length > 0 && containerRef.current) {
      const firstMatch = highlightRefs.current.get(matches[0])
      if (firstMatch) {
        firstMatch.scrollIntoView({ behavior: 'smooth', block: 'center' })
      }
    }
  }, [searchQuery, block])

  const handlePrevMatch = useCallback(() => {
    setCurrentMatchIndex(prev => (prev > 0 ? prev - 1 : searchMatches.length - 1))
  }, [searchMatches.length])

  const handleNextMatch = useCallback(() => {
    setCurrentMatchIndex(prev => (prev < searchMatches.length - 1 ? prev + 1 : 0))
  }, [searchMatches.length])

  useEffect(() => {
    if (searchMatches.length === 0 || !containerRef.current) return
    const matchIndex = searchMatches[currentMatchIndex]
    const row = highlightRefs.current.get(matchIndex)
    if (row) {
      row.scrollIntoView({ behavior: 'smooth', block: 'center' })
    }
  }, [currentMatchIndex, searchMatches])

  if (!block) {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        Select a file to view its diff
      </div>
    )
  }

  const classified = classifyFile(block, threshold)
  const showCollapsedPlaceholder = classified.isCollapsed && !renderAnyway

  return (
    <div className="flex flex-col h-full">
      <div className="sticky top-0 z-10 bg-gray-50 border-b border-gray-200 px-4 py-2 flex items-center gap-3 text-xs font-mono">
        <span className="font-medium text-gray-800 truncate flex-1">{block.newPath || block.oldPath}</span>
        <FileStatusBadge status={block.status} />
        <span className="text-green-600">+{block.additions}</span>
        <span className="text-red-500">-{block.deletions}</span>
      </div>

      <div className="sticky top-[41px] z-10 bg-white border-b border-gray-200 px-4 py-1.5 flex items-center gap-2 text-xs">
        <Input
          type="text"
          placeholder="Search diff..."
          value={searchQuery}
          onChange={e => setSearchQuery(e.target.value)}
          className="h-7 flex-1 text-xs font-mono"
        />
        {searchMatches.length > 0 && (
          <>
            <span className="text-yellow-700">
              {currentMatchIndex + 1} of {searchMatches.length} matches
            </span>
            <Button
              variant="outline"
              size="xs"
              onClick={handlePrevMatch}
              className="border-yellow-300 bg-yellow-100 hover:bg-yellow-200"
            >
              Prev
            </Button>
            <Button
              variant="outline"
              size="xs"
              onClick={handleNextMatch}
              className="border-yellow-300 bg-yellow-100 hover:bg-yellow-200"
            >
              Next
            </Button>
          </>
        )}
      </div>

      <div className="flex-1 overflow-auto" ref={containerRef}>
        {block.isBinary ? (
          <div className="px-4 py-3 text-sm text-gray-500 italic">Binary file, no diff available</div>
        ) : showCollapsedPlaceholder ? (
          <div className="flex flex-col items-center justify-center py-12 px-4">
            <div className="text-sm text-gray-500 mb-2">
              {classified.collapseReason === 'lockfile' && 'Lockfile'}
              {classified.collapseReason === 'generated' && 'Generated file'}
              {classified.collapseReason === 'dependency' && 'Dependency file'}
              {classified.collapseReason === 'large' && 'Large diff'}
              {' — '}{block.changedLineCount} lines changed
            </div>
            <Button
              variant="outline"
              size="sm"
              onClick={onRenderAnyway}
            >
              Render anyway
            </Button>
          </div>
        ) : (
          <table className="w-full text-xs font-mono border-collapse">
            <tbody>
              {block.lines.map((line, i) => {
                let bg = ''
                let textColor = 'text-gray-700'
                if (line.type === 'add') {
                  bg = 'bg-green-50'
                  textColor = 'text-green-800'
                } else if (line.type === 'del') {
                  bg = 'bg-red-50'
                  textColor = 'text-red-800'
                } else if (line.type === 'hunk') {
                  bg = 'bg-blue-50/50'
                  textColor = 'text-blue-600'
                }

                const isMatch = searchMatches.includes(i)
                if (isMatch) {
                  bg = 'bg-yellow-100'
                }

                const oldLineStr = line.type === 'add' ? '' : (line.oldLine?.toString() ?? '')
                const newLineStr = line.type === 'del' ? '' : (line.newLine?.toString() ?? '')

                return (
                  <tr
                    key={i}
                    ref={(el) => {
                      if (el) highlightRefs.current.set(i, el)
                    }}
                    className={`${bg} leading-5`}
                  >
                    <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-300 border-r border-gray-100">
                      {oldLineStr}
                    </td>
                    <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-300 border-r border-gray-100">
                      {newLineStr}
                    </td>
                    <td className={`${textColor} px-3 py-0 whitespace-pre`}>
                      {line.content}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}

function FileStatusBadge({ status }: { status: string }) {
  const colors: Record<string, string> = {
    added: 'text-green-600 bg-green-50',
    modified: 'text-blue-600 bg-blue-50',
    deleted: 'text-red-600 bg-red-50',
    renamed: 'text-purple-600 bg-purple-50',
    binary: 'text-gray-500 bg-gray-50',
  }
  const colorClass = colors[status] ?? 'text-gray-600 bg-gray-50'
  return (
    <span className={`px-1.5 py-0.5 rounded text-xs font-medium ${colorClass}`}>
      {status}
    </span>
  )
}
