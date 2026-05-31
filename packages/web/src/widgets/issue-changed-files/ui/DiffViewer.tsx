import { useState } from 'react'
import { parseDiff, FileBlock } from '../model/diffModel'
import { Button } from '@/shared/ui/components/button'

function FileEntry({ block }: { block: FileBlock }) {
  const [expanded, setExpanded] = useState(false)

  const displayName = block.newPath || block.oldPath

  return (
    <div className="border border-gray-200 rounded-md overflow-hidden">
      <Button
        variant="ghost"
        onClick={() => setExpanded(!expanded)}
        className="h-auto w-full justify-start gap-3 rounded-none px-3 py-2 text-sm hover:bg-gray-50 text-left bg-gray-50/50"
      >
        <svg
          className={`h-3.5 w-3.5 text-gray-400 transition-transform flex-shrink-0 ${expanded ? 'rotate-90' : ''}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path
            fillRule="evenodd"
            d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
            clipRule="evenodd"
          />
        </svg>
        <span className="font-mono text-xs text-gray-800 truncate flex-1">{displayName}</span>
        {block.additions > 0 && (
          <span className="text-green-600 text-xs font-medium flex-shrink-0">+{block.additions}</span>
        )}
        {block.deletions > 0 && (
          <span className="text-red-500 text-xs font-medium flex-shrink-0">-{block.deletions}</span>
        )}
      </Button>

      {expanded && (
        <div className="border-t border-gray-200 overflow-x-auto">
          {block.isBinary ? (
            <div className="px-4 py-3 text-sm text-gray-500 italic">Binary file, no diff available</div>
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
                        {line.type === 'hunk' ? line.content : line.content}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  )
}

interface DiffViewerProps {
  diff: string
}

export function DiffViewer({ diff }: DiffViewerProps) {
  if (!diff || !diff.trim()) return null

  const blocks = parseDiff(diff)
  if (blocks.length === 0) return null

  return (
    <div className="flex flex-col gap-2">
      {blocks.map((block, i) => (
        <FileEntry key={`${block.newPath}-${i}`} block={block} />
      ))}
    </div>
  )
}
