import { useState } from 'react'

type DiffLine = {
  type: 'hunk' | 'add' | 'del' | 'context'
  content: string
  oldLine?: number
  newLine?: number
}

type FileBlock = {
  oldPath: string
  newPath: string
  additions: number
  deletions: number
  isBinary: boolean
  lines: DiffLine[]
}

function parseHunkHeader(line: string): { oldStart: number; newStart: number } | null {
  const match = line.match(/@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/)
  if (!match) return null
  return { oldStart: parseInt(match[1], 10), newStart: parseInt(match[2], 10) }
}

function parseDiff(diffText: string): FileBlock[] {
  if (!diffText.trim()) return []

  const lines = diffText.split('\n')
  const blocks: FileBlock[] = []
  let current: FileBlock | null = null
  let oldLine = 0
  let newLine = 0

  for (const rawLine of lines) {
    if (rawLine.startsWith('diff --git')) {
      if (current) blocks.push(current)
      const match = rawLine.match(/^diff --git a\/(.*) b\/(.*)$/)
      current = {
        oldPath: match?.[1] ?? '',
        newPath: match?.[2] ?? '',
        additions: 0,
        deletions: 0,
        isBinary: false,
        lines: [],
      }
      oldLine = 0
      newLine = 0
      continue
    }

    if (!current) continue

    if (rawLine.startsWith('Binary files')) {
      current.isBinary = true
      continue
    }

    if (rawLine.startsWith('--- ') || rawLine.startsWith('+++ ')) {
      continue
    }

    if (rawLine.startsWith('@@')) {
      const hunk = parseHunkHeader(rawLine)
      if (hunk) {
        oldLine = hunk.oldStart
        newLine = hunk.newStart
      }
      current.lines.push({ type: 'hunk', content: rawLine })
      continue
    }

    if (rawLine.startsWith('+')) {
      current.lines.push({ type: 'add', content: rawLine, newLine })
      current.additions++
      newLine++
    } else if (rawLine.startsWith('-')) {
      current.lines.push({ type: 'del', content: rawLine, oldLine })
      current.deletions++
      oldLine++
    } else if (rawLine.startsWith(' ')) {
      current.lines.push({ type: 'context', content: rawLine, oldLine, newLine })
      oldLine++
      newLine++
    } else if (rawLine === '') {
      current.lines.push({ type: 'context', content: '', oldLine, newLine })
      oldLine++
      newLine++
    }
  }

  if (current) blocks.push(current)
  return blocks
}

function FileEntry({ block }: { block: FileBlock }) {
  const [expanded, setExpanded] = useState(false)

  const displayName = block.newPath || block.oldPath

  return (
    <div className="border border-gray-200 rounded-md overflow-hidden">
      <button
        onClick={() => setExpanded(!expanded)}
        className="w-full flex items-center gap-3 px-3 py-2 text-sm hover:bg-gray-50 transition-colors text-left bg-gray-50/50"
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
      </button>

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
