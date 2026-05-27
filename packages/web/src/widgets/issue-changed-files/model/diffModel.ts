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

export function getFileBlockIdentity(block: Pick<FileBlock, 'oldPath' | 'newPath'>): string {
  return block.newPath || block.oldPath
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

export interface DiffFileMetadata {
  file: string
  oldFile?: string
  additions?: number
  deletions?: number
  diff?: string
  isBinary?: boolean
}

export function fileBlockFromMetadata(file: DiffFileMetadata): FileBlock {
  const oldPath = file.oldFile ?? file.file
  const newPath = file.file
  const isBinary = file.isBinary === true
  const additions = file.additions ?? 0
  const deletions = file.deletions ?? 0

  return {
    oldPath,
    newPath,
    status: inferFileStatus(oldPath, newPath, isBinary),
    isBinary,
    additions,
    deletions,
    hunks: [],
    lines: [],
    changedLineCount: additions + deletions,
    hunkCount: 0,
    rawPatch: file.diff ?? '',
  }
}

export function parseDiffFiles(files: DiffFileMetadata[]): FileBlock[] {
  return files.flatMap(file => {
    const parsed = parseDiff(file.diff ?? '')
    if (parsed.length > 0) {
      return parsed.map(block => ({
        ...block,
        isBinary: block.isBinary || file.isBinary === true,
        additions: block.additions || file.additions || 0,
        deletions: block.deletions || file.deletions || 0,
        changedLineCount: (block.additions || file.additions || 0) + (block.deletions || file.deletions || 0),
      }))
    }
    return [fileBlockFromMetadata(file)]
  })
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
      currentHunk = null
    }
  }

  const finalizeCurrentBlock = () => {
    if (!current) return
    flushHunk()
    current.rawPatch = currentRawPatch
    current.changedLineCount = current.additions + current.deletions
    current.hunkCount = current.hunks.length
    if (current.status === 'modified') {
      current.status = inferFileStatus(current.oldPath, current.newPath, current.isBinary)
    }
    blocks.push(current)
  }

  for (const rawLine of lines) {
    if (rawLine.startsWith('diff --git')) {
      if (current) {
        finalizeCurrentBlock()
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

  if (current) {
    finalizeCurrentBlock()
  }

  return blocks
}

export const DEFAULT_LARGE_DIFF_THRESHOLD = 300

export function isLargeDiff(block: FileBlock, threshold: number = DEFAULT_LARGE_DIFF_THRESHOLD): boolean {
  return block.changedLineCount > threshold
}

const GENERATED_PATTERNS = [
  /^[^/]*\/node_modules\//,
  /^node_modules\//,
  /\.lock$/,
  /(^|\/)package-lock\.json$/,
  /(^|\/)yarn\.lock$/,
  /(^|\/)pnpm-lock\.yaml$/,
  /(^|\/)composer\.lock$/,
  /(^|\/)Cargo\.lock$/,
  /(^|\/)Gemfile\.lock$/,
  /(^|\/) Pipfile\.lock$/,
  /\.min\.(js|css)$/,
  /\.bundle\.(js|css)$/,
  /\.map$/,
  /(^|\/)dist\//,
  /(^|\/)build\//,
  /(^|\/)coverage\//,
  /(^|\/)\.next\//,
  /(^|\/)\.nuxt\//,
  /(^|\/)__pycache__\//,
  /\.pyc$/,
]

export function isGeneratedFile(path: string): boolean {
  return GENERATED_PATTERNS.some(pattern => pattern.test(path))
}

export function isLockfile(path: string): boolean {
  return /(^|\/)(package-lock\.json|yarn\.lock|pnpm-lock\.yaml|composer\.lock|Cargo\.lock|Gemfile\.lock|Pipfile\.lock)$/.test(path)
}

export function isDependencyHeavy(path: string): boolean {
  return isLockfile(path) || /\.lock$/.test(path)
}

export function isBinaryFile(block: FileBlock): boolean {
  return block.isBinary
}

export type CollapseReason = 'generated' | 'lockfile' | 'dependency' | 'large' | null

export interface ClassifiedFile {
  block: FileBlock
  isReadable: boolean
  isCollapsed: boolean
  collapseReason: CollapseReason
  displayPath: string
}

export function classifyFile(block: FileBlock, threshold: number = DEFAULT_LARGE_DIFF_THRESHOLD): ClassifiedFile {
  const displayPath = getFileBlockIdentity(block)
  const isGen = isGeneratedFile(displayPath)
  const isLock = isLockfile(displayPath)
  const isDep = isDependencyHeavy(displayPath)
  const isLarge = isLargeDiff(block, threshold)
  const isBin = isBinaryFile(block)

  let collapseReason: CollapseReason = null
  if (isBin) collapseReason = null
  else if (isLock) collapseReason = 'lockfile'
  else if (isDep) collapseReason = 'dependency'
  else if (isGen) collapseReason = 'generated'
  else if (isLarge) collapseReason = 'large'

  const isCollapsible = collapseReason !== null

  return {
    block,
    isReadable: !isBin && !isCollapsible,
    isCollapsed: isCollapsible,
    collapseReason,
    displayPath,
  }
}

export function selectFirstReadableFile(
  blocks: FileBlock[],
  threshold: number = DEFAULT_LARGE_DIFF_THRESHOLD
): FileBlock | null {
  for (const block of blocks) {
    const classified = classifyFile(block, threshold)
    if (classified.isReadable) {
      return block
    }
  }
  return null
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
