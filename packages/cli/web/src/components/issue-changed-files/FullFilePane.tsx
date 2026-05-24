import { useState, useEffect, useRef } from 'react'
import { api } from '../../lib/api'
import type { FileBlock } from '../../lib/diffModel'
import { classifyFile, DEFAULT_LARGE_DIFF_THRESHOLD } from '../../lib/diffModel'
import { useProject } from '../../context/ProjectContext'

interface FullFilePaneProps {
  block: FileBlock | null
  issueNumber: number
  onClose?: () => void
  threshold?: number
  renderAnyway?: boolean
  onRenderAnyway?: () => void
}

interface FileContent {
  base: string
  head: string
}

export function FullFilePane({ block, issueNumber, threshold = DEFAULT_LARGE_DIFF_THRESHOLD, renderAnyway = false, onRenderAnyway }: FullFilePaneProps) {
  const [content, setContent] = useState<FileContent | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const { projectId } = useProject()
  const containerRef = useRef<HTMLDivElement>(null)
  const classified = block ? classifyFile(block, threshold) : null
  const showCollapsedPlaceholder = !!classified?.isCollapsed && !renderAnyway

  useEffect(() => {
    if (!block || block.status === 'binary') {
      setContent(null)
      return
    }

    if (showCollapsedPlaceholder) {
      setContent(null)
      setLoading(false)
      setError(null)
      return
    }

    const path = block.newPath || block.oldPath
    if (!path) return

    setLoading(true)
    setError(null)

    api.getFileContent(issueNumber, path, projectId)
      .then((data) => {
        setContent(data)
        setLoading(false)
      })
      .catch((err) => {
        setError(err instanceof Error ? err.message : 'Failed to load file content')
        setLoading(false)
      })
  }, [block, issueNumber, projectId, showCollapsedPlaceholder])

  if (!block) {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        Select a file to view its content
      </div>
    )
  }

  if (block.isBinary || block.status === 'binary') {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        Binary file, no diff available
      </div>
    )
  }

  if (showCollapsedPlaceholder) {
    return (
      <div className="flex flex-col h-full">
        <div className="sticky top-0 z-10 bg-gray-50 border-b border-gray-200 px-4 py-2 flex items-center gap-3 text-xs font-mono">
          <span className="font-medium text-gray-800 truncate flex-1">{block.newPath || block.oldPath}</span>
          <FileStatusBadge status={block.status} />
          <span className="text-green-600">+{block.additions}</span>
          <span className="text-red-500">-{block.deletions}</span>
        </div>
        <div className="flex flex-col items-center justify-center py-12 px-4 flex-1">
          <div className="text-sm text-gray-500 mb-2">
            {classified.collapseReason === 'lockfile' && 'Lockfile'}
            {classified.collapseReason === 'generated' && 'Generated file'}
            {classified.collapseReason === 'dependency' && 'Dependency file'}
            {classified.collapseReason === 'large' && 'Large diff'}
            {' — '}{block.changedLineCount} lines changed
          </div>
          <button
            onClick={onRenderAnyway}
            className="px-3 py-1.5 text-sm bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors"
          >
            Render anyway
          </button>
        </div>
      </div>
    )
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        Loading file content...
      </div>
    )
  }

  if (error) {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        {error}
      </div>
    )
  }

  if (!content) {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        No content available
      </div>
    )
  }

  return (
    <div className="flex flex-col h-full">
      <div className="sticky top-0 z-10 bg-gray-50 border-b border-gray-200 px-4 py-2 flex items-center gap-3 text-xs font-mono">
        <span className="font-medium text-gray-800 truncate flex-1">{block.newPath || block.oldPath}</span>
        <FileStatusBadge status={block.status} />
        <span className="text-green-600">+{block.additions}</span>
        <span className="text-red-500">-{block.deletions}</span>
      </div>
      <div className="flex-1 overflow-auto" ref={containerRef}>
        <table className="w-full text-xs font-mono border-collapse">
          <tbody>
            {renderFullFileContent(content, block)}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function renderFullFileContent(content: FileContent, block: FileBlock): React.ReactNode[] {
  const baseLines = content.base.split('\n')
  const headLines = content.head.split('\n')

  const changedBaseLines = new Set<number>()
  const changedHeadLines = new Set<number>()
  for (const line of block.lines) {
    if (line.type === 'del' && line.oldLine !== undefined) {
      changedBaseLines.add(line.oldLine)
    } else if (line.type === 'add' && line.newLine !== undefined) {
      changedHeadLines.add(line.newLine)
    }
  }

  const maxLines = Math.max(baseLines.length, headLines.length)
  const rows: React.ReactNode[] = []

  for (let i = 0; i < maxLines; i++) {
    const baseLine = baseLines[i] ?? ''
    const headLine = headLines[i] ?? ''
    const lineNum = i + 1
    const isBaseChanged = changedBaseLines.has(lineNum)
    const isHeadChanged = changedHeadLines.has(lineNum)

    rows.push(
      <tr key={`base-${i}`} className={`leading-5 ${isBaseChanged ? 'bg-red-50' : ''}`}>
        <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-300 border-r border-gray-100">
          {lineNum}
        </td>
        <td className={`px-3 py-0 whitespace-pre border-r border-gray-100 ${isBaseChanged ? 'bg-red-50 text-red-800' : 'text-gray-700'}`}>
          {baseLine}
        </td>
      </tr>
    )

    if (baseLine !== headLine) {
      rows.push(
        <tr key={`head-${i}`} className={`leading-5 ${isHeadChanged ? 'bg-green-50' : ''}`}>
          <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-300 border-r border-gray-100">
            {lineNum}
          </td>
          <td className={`px-3 py-0 whitespace-pre ${isHeadChanged ? 'bg-green-50 text-green-800' : 'text-gray-700'}`}>
            {headLine}
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
