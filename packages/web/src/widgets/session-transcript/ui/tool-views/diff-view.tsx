import { useState } from 'react'
import { Button } from '@/shared/ui/components/button'
import type { DisplayChangedFile } from '../../model/session-transcript-display'
import { parseJsonSafely, parseEditInput } from '../../model/transcript-tool-utils'
import { buildDiffFromEdit, buildDiffFromPatchText, type FileBlock } from '../../model/diff-builder'
import { parseDiff, isLargeDiff } from '@/shared/lib/diff-model'

interface PatchDiffViewProps {
  changedFiles: DisplayChangedFile[]
}

export function PatchDiffView({ changedFiles }: PatchDiffViewProps) {
  const [expanded, setExpanded] = useState(false)

  if (changedFiles.length === 0) return null

  const hasRawDetail = changedFiles.some(f => f.rawDetail)

  let diffBlocks: FileBlock[] = []
  if (hasRawDetail) {
    const rawDetail = changedFiles.find(f => f.rawDetail)?.rawDetail
    if (rawDetail && typeof rawDetail === 'string') {
      if (rawDetail.includes('---')) {
        diffBlocks = parseDiff(rawDetail)
      } else if (rawDetail.includes('*** ')) {
        diffBlocks = buildDiffFromPatchText(rawDetail)
      }
    }
  }

  return (
    <div className="border-t border-gray-100">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 text-xs text-blue-600 hover:text-blue-800 hover:bg-gray-50 transition-colors rounded-none"
      >
        <svg className={`h-3 w-3 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
        {expanded ? 'Hide' : 'Show'} diff {hasRawDetail && '(expanded view)'}
      </Button>
      {expanded && (
        <div className="px-3 pb-2 space-y-2">
          {changedFiles.map((change, i) => {
            const opBadge: Record<string, string> = { created: '+', modified: '~', deleted: '-', moved: '>' }
            return (
              <div key={i} className="space-y-1">
                <div className="flex items-center gap-2 py-0.5">
                  <span className="text-xs font-mono text-gray-500 w-3">{opBadge[change.operation] ?? '?'}</span>
                  <span className="text-xs font-mono text-gray-700 truncate flex-1">{change.path}</span>
                  {change.additions !== undefined && change.additions > 0 && (
                    <span className="text-xs text-green-600">+{change.additions}</span>
                  )}
                  {change.deletions !== undefined && change.deletions > 0 && (
                    <span className="text-xs text-red-600">-{change.deletions}</span>
                  )}
                </div>
                {change.rawDetail && diffBlocks.length > 0 && (
                  <div className="pl-4">
                    {diffBlocks.slice(0, 3).map((block, j) => (
                      <DiffBlockView key={j} block={block} />
                    ))}
                  </div>
                )}
                {change.rawDetail && diffBlocks.length === 0 && (
                  <pre className="text-xs font-mono text-gray-600 bg-gray-50 rounded p-2 whitespace-pre-wrap break-all max-h-32 overflow-auto">
                    {change.rawDetail}
                  </pre>
                )}
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}

interface DiffContentViewProps {
  changedFiles?: DisplayChangedFile[]
  rawInput?: string
  rawOutput?: string
  details?: Record<string, unknown>
  normalizedName: string
}

export function DiffContentView({ changedFiles, rawInput, rawOutput, details, normalizedName }: DiffContentViewProps) {
  const [showRaw, setShowRaw] = useState(false)

  let diffBlocks: FileBlock[] = []
  let diffText: string | undefined
  const metadataDiff = details?.family === 'mutation' && Array.isArray(details.files)
    ? details.files.find((file) => file && typeof file === 'object' && typeof (file as Record<string, unknown>).diff === 'string')
    : undefined
  const metadataDiffText = metadataDiff && typeof metadataDiff === 'object' ? (metadataDiff as Record<string, unknown>).diff : undefined

  if (typeof metadataDiffText === 'string' && metadataDiffText) {
    diffText = metadataDiffText
  } else if (rawOutput && typeof rawOutput === 'string' && rawOutput.includes('---')) {
    diffText = rawOutput
  } else if (rawInput && typeof rawInput === 'string' && rawInput.includes('---')) {
    diffText = rawInput
  }

  if (diffText) {
    diffBlocks = parseDiff(diffText)
  } else if ((normalizedName === 'edit' || normalizedName === 'write') && rawInput) {
    const editInput = parseEditInput(rawInput)
    if (editInput && editInput.oldString && editInput.newString && editInput.filePath) {
      const fileName = editInput.filePath.split('/').pop() ?? editInput.filePath
      diffBlocks = buildDiffFromEdit(fileName, editInput.oldString, editInput.newString)
    }
  } else if (normalizedName === 'apply_patch' && rawInput) {
    const parsed = parseJsonSafely(rawInput)
    if (parsed) {
      const patchText = parsed.patchText ?? parsed.patch
      if (typeof patchText === 'string' && patchText.includes('*** ')) {
        diffBlocks = buildDiffFromPatchText(patchText)
      }
    }
  }

  const hasDiff = diffBlocks.length > 0
  const displayFiles = changedFiles && changedFiles.length > 0
    ? changedFiles
    : diffBlocks.length > 0
      ? diffBlocks.map(b => ({
          path: b.newPath || b.oldPath,
          operation: (b.status === 'added' ? 'created' : b.status === 'deleted' ? 'deleted' : b.status === 'renamed' ? 'moved' : 'modified') as DisplayChangedFile['operation'],
          additions: b.additions,
          deletions: b.deletions,
        }))
      : []

  return (
    <div className="border-t border-gray-100">
      {displayFiles.length > 0 && (
        <div className="px-3 pt-2">
          <div className="flex items-center justify-between mb-1.5">
            <span className="text-xs font-medium text-gray-500">
              Changed files ({displayFiles.length})
            </span>
            {hasDiff && (
              <Button
                variant="link"
                onClick={() => setShowRaw(!showRaw)}
                className="h-auto p-0 text-xs text-gray-400 hover:text-gray-600 transition-colors"
              >
                {showRaw ? 'Show diff' : 'Show raw'}
              </Button>
            )}
          </div>
          <div className="space-y-1">
            {displayFiles.slice(0, 5).map((file, i) => {
              const opBadge: Record<string, string> = { created: '+', modified: '~', deleted: '-', moved: '>' }
              return (
                <div key={i} className="flex items-center gap-2 py-0.5 px-1.5 bg-gray-50 rounded">
                  <span className="text-xs font-mono text-gray-500 w-3">{opBadge[file.operation] ?? '?'}</span>
                  <span className="text-xs font-mono text-gray-700 truncate flex-1">{file.path}</span>
                  {file.additions !== undefined && file.additions > 0 && (
                    <span className="text-xs text-green-600">+{file.additions}</span>
                  )}
                  {file.deletions !== undefined && file.deletions > 0 && (
                    <span className="text-xs text-red-600">-{file.deletions}</span>
                  )}
                </div>
              )
            })}
            {displayFiles.length > 5 && (
              <div className="text-xs text-gray-400 px-1.5">...and {displayFiles.length - 5} more</div>
            )}
          </div>
        </div>
      )}

      {hasDiff && !showRaw && (
        <div className="px-3 pb-2">
          <div className="mt-2 space-y-2">
            {diffBlocks.map((block, i) => (
              <DiffBlockView key={i} block={block} />
            ))}
          </div>
        </div>
      )}

      {showRaw && (
        <div className="px-3 pb-2">
          <div className="font-medium text-xs text-gray-500 mb-1">Raw output</div>
          <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
            {diffText}
          </pre>
        </div>
      )}

      {!hasDiff && !showRaw && (
        <div className="px-3 pb-2">
          {rawInput && !rawInput.includes('---') && (
            <div className="mb-2">
              <div className="font-medium text-xs text-gray-500 mb-1">Input</div>
              <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-24 overflow-auto">
                {rawInput}
              </pre>
            </div>
          )}
          {rawOutput && (
            <div>
              <div className="font-medium text-xs text-gray-500 mb-1">Output</div>
              <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-24 overflow-auto">
                {rawOutput}
              </pre>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function DiffBlockView({ block }: { block: FileBlock }) {
  const [expanded, setExpanded] = useState(false)
  const large = isLargeDiff(block, 200)

  return (
    <div className="rounded border border-gray-200 overflow-hidden">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-2 py-1 hover:bg-gray-50 transition-colors text-xs rounded-none"
      >
        <svg className={`h-3 w-3 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
        <span className="font-mono text-gray-700 truncate flex-1">{block.newPath || block.oldPath}</span>
        <span className="text-green-600">+{block.additions}</span>
        <span className="text-red-500">-{block.deletions}</span>
      </Button>
      {expanded && (
        <div className="border-t border-gray-100">
          {large ? (
            <div className="px-3 py-2 text-xs text-gray-400 text-center">
              Large diff ({block.changedLineCount} lines) — truncated for display
            </div>
          ) : (
            <table className="w-full text-xs font-mono">
              <tbody>
                {block.lines.slice(0, 100).map((line, i) => {
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
                  return (
                    <tr key={i} className={bg}>
                      <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-300 border-r border-gray-100">
                        {line.oldLine?.toString() ?? ''}
                      </td>
                      <td className="w-[1%] whitespace-nowrap select-none text-right px-2 py-0 text-gray-300 border-r border-gray-100">
                        {line.newLine?.toString() ?? ''}
                      </td>
                      <td className={`${textColor} px-3 py-0 whitespace-pre`}>
                        {line.content}
                      </td>
                    </tr>
                  )
                })}
                {block.lines.length > 100 && (
                  <tr>
                    <td colSpan={3} className="px-3 py-1 text-xs text-gray-400 text-center">
                      ... {block.lines.length - 100} more lines
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  )
}
