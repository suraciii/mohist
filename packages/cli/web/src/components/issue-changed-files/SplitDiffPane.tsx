import { useState, useRef } from 'react'
import type { FileBlock, DiffLine } from '../../lib/diffModel'
import { isLargeDiff, DEFAULT_LARGE_DIFF_THRESHOLD } from '../../lib/diffModel'

interface SplitDiffPaneProps {
  block: FileBlock | null
  threshold?: number
  activeHunkIndex: number
  onActiveHunkChange?: (index: number) => void
  totalHunks?: number
}

export function SplitDiffPane({
  block,
  threshold = DEFAULT_LARGE_DIFF_THRESHOLD,
  activeHunkIndex,
}: SplitDiffPaneProps) {
  const [renderAnyway, setRenderAnyway] = useState<Set<string>>(new Set())
  const containerRef = useRef<HTMLDivElement>(null)

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
              {renderSplitLines(block.lines, activeHunkIndex)}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}

function renderSplitLines(lines: DiffLine[], activeHunkIndex: number): React.ReactNode[] {
  const rows: React.ReactNode[] = []
  let hunkCount = 0

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]

    if (line.type === 'hunk') {
      const isActive = hunkCount === activeHunkIndex
      rows.push(
        <tr key={`hunk-${hunkCount}-${i}`} className={isActive ? 'bg-blue-100' : 'bg-blue-50/50'}>
          <td colSpan={2} className="px-3 py-0.5 text-blue-600 border-r border-gray-100">{line.content}</td>
          <td colSpan={2} className="px-3 py-0.5 text-blue-600 border-r border-gray-100">{line.content}</td>
        </tr>
      )
      hunkCount++
    } else if (line.type === 'add') {
      rows.push(
        <tr key={`add-${i}`} className="leading-5">
          <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-300 border-r border-gray-100"></td>
          <td className="px-3 py-0 whitespace-pre border-r border-gray-100"></td>
          <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-400 border-r border-gray-100">
            {line.newLine?.toString() ?? ''}
          </td>
          <td className="bg-green-50 text-green-800 px-3 py-0 whitespace-pre">
            {line.content}
          </td>
        </tr>
      )
    } else if (line.type === 'del') {
      rows.push(
        <tr key={`del-${i}`} className="leading-5">
          <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-400 border-r border-gray-100">
            {line.oldLine?.toString() ?? ''}
          </td>
          <td className="bg-red-50 text-red-800 px-3 py-0 whitespace-pre border-r border-gray-100">
            {line.content}
          </td>
          <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-300 border-r border-gray-100"></td>
          <td className="px-3 py-0 whitespace-pre"></td>
        </tr>
      )
    } else {
      rows.push(
        <tr key={`ctx-${i}`} className="leading-5">
          <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-400 border-r border-gray-100">
            {line.oldLine?.toString() ?? ''}
          </td>
          <td className="text-gray-700 px-3 py-0 whitespace-pre border-r border-gray-100">
            {line.content}
          </td>
          <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-400 border-r border-gray-100">
            {line.newLine?.toString() ?? ''}
          </td>
          <td className="text-gray-700 px-3 py-0 whitespace-pre">
            {line.content}
          </td>
        </tr>
      )
    }
  }

  return rows
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