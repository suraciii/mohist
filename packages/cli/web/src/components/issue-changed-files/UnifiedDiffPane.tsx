import { useState, useRef, useEffect } from 'react'
import type { FileBlock } from '../../lib/diffModel'
import { isLargeDiff, DEFAULT_LARGE_DIFF_THRESHOLD } from '../../lib/diffModel'

interface UnifiedDiffPaneProps {
  block: FileBlock | null
  threshold?: number
  activeHunkIndex?: number
  onActiveHunkChange?: (index: number) => void
  totalHunks?: number
}

export function UnifiedDiffPane({
  block,
  threshold = DEFAULT_LARGE_DIFF_THRESHOLD,
  activeHunkIndex = 0,
  onActiveHunkChange,
  totalHunks = 0,
}: UnifiedDiffPaneProps) {
  const [renderAnyway, setRenderAnyway] = useState<Set<string>>(new Set())
  const containerRef = useRef<HTMLDivElement>(null)

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

  if (!block) {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        Select a file to view its diff
      </div>
    )
  }

  const large = isLargeDiff(block, threshold)
  const showLargePlaceholder = large && !renderAnyway.has(block.newPath)

  return (
    <div className="flex flex-col h-full">
      <div className="sticky top-0 z-10 bg-gray-50 border-b border-gray-200 px-4 py-2 flex items-center gap-3 text-xs font-mono">
        <span className="font-medium text-gray-800 truncate flex-1">{block.newPath || block.oldPath}</span>
        <FileStatusBadge status={block.status} />
        <span className="text-green-600">+{block.additions}</span>
        <span className="text-red-500">-{block.deletions}</span>
      </div>

      <div className="flex-1 overflow-auto" ref={containerRef}>
        {block.isBinary ? (
          <div className="px-4 py-3 text-sm text-gray-500 italic">Binary file, no diff available</div>
        ) : showLargePlaceholder ? (
          <div className="flex flex-col items-center justify-center py-12 px-4">
            <div className="text-sm text-gray-500 mb-2">
              Large diff — {block.changedLineCount} lines changed
            </div>
            <button
              onClick={() => setRenderAnyway(prev => new Set(prev).add(block.newPath))}
              className="px-3 py-1.5 text-sm bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors"
            >
              Render anyway
            </button>
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

                const oldLineStr = line.type === 'add' ? '' : (line.oldLine?.toString() ?? '')
                const newLineStr = line.type === 'del' ? '' : (line.newLine?.toString() ?? '')

                return (
                  <tr key={i} className={`${bg} leading-5`}>
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

export { FileStatusBadge }