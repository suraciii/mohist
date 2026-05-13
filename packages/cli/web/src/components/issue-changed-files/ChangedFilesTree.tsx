import { useState, useMemo, useContext, createContext, useEffect, useRef } from 'react'
import type { FileBlock } from '../../lib/diffModel'

interface DirectoryNode {
  name: string
  children: Map<string, DirectoryNode | FileBlock>
  files: FileBlock[]
}

function buildDirectoryTree(blocks: FileBlock[]): Map<string, DirectoryNode> {
  const root = new Map<string, DirectoryNode>()

  for (const block of blocks) {
    const path = block.newPath || block.oldPath
    const parts = path.split('/')
    const firstPart = parts[0]
    const restParts = parts.slice(1)

    let dir = root.get(firstPart)
    if (!dir) {
      dir = { name: firstPart, children: new Map(), files: [] }
      root.set(firstPart, dir)
    }

    let current = dir
    if (restParts.length === 0) {
      current.files.push(block)
    } else {
      for (let i = 0; i < restParts.length; i++) {
        const part = restParts[i]
        const isLast = i === restParts.length - 1

        if (isLast) {
          current.files.push(block)
        } else {
          let child = current.children.get(part)
          if (!child || !('children' in child)) {
            child = { name: part, children: new Map(), files: [] }
            current.children.set(part, child)
          }
          current = child as DirectoryNode
        }
      }
    }
  }

  return root
}

interface FileFilterContextValue {
  filter: string
  setFilter: (f: string) => void
  selectedFile: FileBlock | null
  setSelectedFile: (b: FileBlock | null) => void
}

export const FileFilterContext = createContext<FileFilterContextValue>({
  filter: '',
  setFilter: () => {},
  selectedFile: null,
  setSelectedFile: () => {},
})

interface ChangedFilesTreeProps {
  blocks: FileBlock[]
  selectedFile: FileBlock | null
  onSelectFile: (block: FileBlock) => void
  expandState: 'all' | 'none' | 'mixed'
}

function sortMapKeys(m: Map<string, unknown>): string[] {
  return Array.from(m.keys()).sort()
}

export function ChangedFilesTree({ blocks, selectedFile, onSelectFile, expandState }: ChangedFilesTreeProps) {
  const [filter, setFilter] = useState('')

  const filteredBlocks = useMemo(() => {
    if (!filter.trim()) return blocks
    const q = filter.toLowerCase()
    return blocks.filter(b => {
      const path = b.newPath || b.oldPath
      return path.toLowerCase().includes(q)
    })
  }, [blocks, filter])

  const tree = useMemo(() => buildDirectoryTree(filteredBlocks), [filteredBlocks])

  const dirs = sortMapKeys(tree)

  return (
    <FileFilterContext.Provider value={{ filter, setFilter, selectedFile, setSelectedFile: (b) => onSelectFile(b as FileBlock) }}>
      <div className="flex flex-col h-full">
        <div className="px-3 py-2 border-b border-gray-200">
          <input
            type="text"
            placeholder="Filter files..."
            value={filter}
            onChange={e => setFilter(e.target.value)}
            className="w-full px-2 py-1 text-sm border border-gray-200 rounded focus:outline-none focus:ring-1 focus:ring-blue-500"
          />
        </div>

        <div className="flex-1 overflow-y-auto">
          {dirs.length === 0 ? (
            <div className="px-3 py-4 text-sm text-gray-400 text-center">
              {filter ? 'No matching files' : 'No files changed'}
            </div>
          ) : (
            dirs.map(dirName => (
              <DirectoryGroup
                key={dirName}
                name={dirName}
                node={tree.get(dirName)!}
                expandState={expandState}
              />
            ))
          )}
        </div>
      </div>
    </FileFilterContext.Provider>
  )
}

function DirectoryGroup({ name, node, expandState, depth = 0 }: { name: string; node: DirectoryNode; expandState: 'all' | 'none' | 'mixed'; depth?: number }) {
  const [collapsed, setCollapsed] = useState(false)
  const prevExpandState = useRef(expandState)

  useEffect(() => {
    if (prevExpandState.current !== expandState) {
      if (expandState === 'all') {
        setCollapsed(false)
      } else if (expandState === 'none') {
        setCollapsed(true)
      }
    }
    prevExpandState.current = expandState
  }, [expandState])

  const childDirs = sortMapKeys(node.children).filter(k => 'children' in node.children.get(k)!)

  return (
    <div>
      <button
        onClick={() => setCollapsed(!collapsed)}
        className="w-full flex items-center gap-1 px-3 py-1 text-xs font-medium text-gray-600 hover:bg-gray-50 transition-colors"
        style={{ paddingLeft: `${depth * 12 + 12}px` }}
      >
        <svg
          className={`h-3 w-3 text-gray-400 transition-transform ${collapsed ? '' : 'rotate-90'}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path
            fillRule="evenodd"
            d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
            clipRule="evenodd"
          />
        </svg>
        <span>{name}/</span>
        <span className="text-gray-400">({node.files.length})</span>
      </button>

      {!collapsed && (
        <>
          {node.files.map((block, i) => (
            <FileTreeEntry key={`${block.newPath}-${i}`} block={block} depth={depth + 1} />
          ))}
          {childDirs.map(dirName => (
            <DirectoryGroup
              key={dirName}
              name={dirName}
              node={node.children.get(dirName) as DirectoryNode}
              expandState={expandState}
              depth={depth + 1}
            />
          ))}
        </>
      )}
    </div>
  )
}

function FileTreeEntry({ block, depth }: { block: FileBlock; depth: number }) {
  const { selectedFile, setSelectedFile } = useContext(FileFilterContext)

  const displayName = block.newPath || block.oldPath
  const fileName = displayName.split('/').pop() || displayName
  const isSelected = selectedFile?.newPath === block.newPath

  return (
    <button
      onClick={() => setSelectedFile(block)}
      className={`w-full flex items-center gap-2 px-3 py-1 text-xs text-left transition-colors ${
        isSelected ? 'bg-blue-50 text-blue-700' : 'hover:bg-gray-50 text-gray-700'
      }`}
      style={{ paddingLeft: `${depth * 12 + 24}px` }}
    >
      <span className="font-mono truncate flex-1">{fileName}</span>
      {block.additions > 0 && <span className="text-green-600">+{block.additions}</span>}
      {block.deletions > 0 && <span className="text-red-500">-{block.deletions}</span>}
    </button>
  )
}