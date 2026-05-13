export type FileStatus = 'added' | 'modified' | 'deleted' | 'renamed' | 'binary'

export type DiffLineType = 'hunk' | 'add' | 'del' | 'context'

export interface DiffLine {
  type: DiffLineType
  content: string
  oldLine?: number
  newLine?: number
}

export interface DiffHunk {
  header: string
  oldStart: number
  oldCount: number
  newStart: number
  newCount: number
  lines: DiffLine[]
}

export interface FileBlock {
  oldPath: string
  newPath: string
  status: FileStatus
  isBinary: boolean
  additions: number
  deletions: number
  hunks: DiffHunk[]
  lines: DiffLine[]
  changedLineCount: number
  hunkCount: number
  rawPatch?: string
}

function parseHunkHeader(line: string): { oldStart: number; oldCount: number; newStart: number; newCount: number } | null {
  const match = line.match(/@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/)
  if (!match) return null
  return {
    oldStart: parseInt(match[1], 10),
    oldCount: match[2] ? parseInt(match[2], 10) : 1,
    newStart: parseInt(match[3], 10),
    newCount: match[4] ? parseInt(match[4], 10) : 1,
  }
}

function inferFileStatus(oldPath: string, newPath: string, isBinary: boolean): FileStatus {
  if (isBinary) return 'binary'
  if (oldPath === '/dev/null') return 'added'
  if (newPath === '/dev/null') return 'deleted'
  if (oldPath !== newPath) return 'renamed'
  return 'modified'
}

export interface ParseOptions {
  includeContext?: boolean
}

export function parseDiff(diffText: string, _options?: ParseOptions): FileBlock[] {
  if (!diffText.trim()) return []

  const lines = diffText.split('\n')
  const blocks: FileBlock[] = []
  let current: FileBlock | null = null
  let currentRawPatch = ''
  let oldLine = 0
  let newLine = 0
  let currentHunk: DiffHunk | null = null

  const flushHunk = () => {
    if (currentHunk && current) {
      current.hunks.push(currentHunk)
      current.lines.push(...currentHunk.lines)
      currentHunk = null
    }
  }

  for (const rawLine of lines) {
    if (rawLine.startsWith('diff --git')) {
      flushHunk()
      if (current) {
        current.rawPatch = currentRawPatch
        blocks.push(current)
      }
      const match = rawLine.match(/^diff --git a\/(.*) b\/(.*)$/)
      current = {
        oldPath: match?.[1] ?? '',
        newPath: match?.[2] ?? '',
        status: 'modified',
        additions: 0,
        deletions: 0,
        isBinary: false,
        hunks: [],
        lines: [],
        changedLineCount: 0,
        hunkCount: 0,
        rawPatch: '',
      }
      currentRawPatch = rawLine + '\n'
      oldLine = 0
      newLine = 0
      continue
    }

    if (!current) continue

    currentRawPatch += rawLine + '\n'

    if (rawLine.startsWith('Binary files')) {
      current.isBinary = true
      current.status = 'binary'
      continue
    }

    if (rawLine.startsWith('new file mode')) {
      current.status = 'added'
      continue
    }

    if (rawLine.startsWith('deleted file mode')) {
      current.status = 'deleted'
      continue
    }

    if (rawLine.startsWith('rename from')) {
      current.status = 'renamed'
      continue
    }

    if (rawLine.startsWith('--- ') || rawLine.startsWith('+++ ')) {
      continue
    }

    if (rawLine.startsWith('@@')) {
      flushHunk()
      const hunk = parseHunkHeader(rawLine)
      if (hunk) {
        oldLine = hunk.oldStart
        newLine = hunk.newStart
        currentHunk = {
          header: rawLine,
          oldStart: hunk.oldStart,
          oldCount: hunk.oldCount,
          newStart: hunk.newStart,
          newCount: hunk.newCount,
          lines: [],
        }
        current.lines.push({ type: 'hunk', content: rawLine })
      }
      continue
    }

    if (rawLine.startsWith('+')) {
      const line: DiffLine = { type: 'add', content: rawLine, newLine }
      if (currentHunk) {
        currentHunk.lines.push(line)
      }
      current.lines.push(line)
      current.additions++
      newLine++
    } else if (rawLine.startsWith('-')) {
      const line: DiffLine = { type: 'del', content: rawLine, oldLine }
      if (currentHunk) {
        currentHunk.lines.push(line)
      }
      current.lines.push(line)
      current.deletions++
      oldLine++
    } else if (rawLine.startsWith(' ')) {
      const line: DiffLine = { type: 'context', content: rawLine, oldLine, newLine }
      if (currentHunk) {
        currentHunk.lines.push(line)
      }
      current.lines.push(line)
      oldLine++
      newLine++
    } else if (rawLine === '') {
      const line: DiffLine = { type: 'context', content: '', oldLine, newLine }
      if (currentHunk) {
        currentHunk.lines.push(line)
      }
      current.lines.push(line)
      oldLine++
      newLine++
    }
  }

  flushHunk()
  if (current) {
    current.rawPatch = currentRawPatch
    current.changedLineCount = current.additions + current.deletions
    current.hunkCount = current.hunks.length
    if (current.status === 'modified') {
      current.status = inferFileStatus(current.oldPath, current.newPath, current.isBinary)
    }
    blocks.push(current)
  }

  return blocks
}

export const DEFAULT_LARGE_DIFF_THRESHOLD = 300

export function isLargeDiff(block: FileBlock, threshold: number = DEFAULT_LARGE_DIFF_THRESHOLD): boolean {
  return block.changedLineCount > threshold
}

export function getDiffStats(blocks: FileBlock[]) {
  return blocks.reduce(
    (acc, block) => ({
      filesChanged: acc.filesChanged + 1,
      additions: acc.additions + block.additions,
      deletions: acc.deletions + block.deletions,
    }),
    { filesChanged: 0, additions: 0, deletions: 0 }
  )
}
