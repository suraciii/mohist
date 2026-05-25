import type { FileBlock } from '../../lib/diffModel'
import { classifyFile, DEFAULT_LARGE_DIFF_THRESHOLD } from '../../lib/diffModel'

interface RawPatchPaneProps {
  block: FileBlock | null
  rawPatch: string
  threshold?: number
  renderAnyway?: boolean
  onRenderAnyway?: () => void
}

export function RawPatchPane({ block, rawPatch, threshold = DEFAULT_LARGE_DIFF_THRESHOLD, renderAnyway = false, onRenderAnyway }: RawPatchPaneProps) {
  if (!rawPatch) {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        No patch available for this file
      </div>
    )
  }

  if (!block) {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        Select a file to view its patch
      </div>
    )
  }

  const classified = classifyFile(block, threshold)
  const showCollapsedPlaceholder = classified.isCollapsed && !renderAnyway

  return (
    <div className="flex flex-col h-full">
      <div className="sticky top-0 z-10 bg-gray-50 border-b border-gray-200 px-4 py-2 flex items-center justify-between text-xs">
        <span className="font-medium text-gray-700">Raw patch</span>
        {!showCollapsedPlaceholder && (
          <button
            onClick={() => navigator.clipboard.writeText(rawPatch)}
            className="px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 rounded border border-gray-200 transition-colors"
          >
            Copy
          </button>
        )}
      </div>
      <div className="flex-1 overflow-auto">
        {showCollapsedPlaceholder ? (
          <div className="flex flex-col items-center justify-center py-12 px-4">
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
        ) : (
          <pre className="text-xs font-mono p-4 whitespace-pre-wrap break-all text-gray-700">
            {rawPatch}
          </pre>
        )}
      </div>
    </div>
  )
}
