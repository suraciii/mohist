// Pure parsers for git CLI output produced by the runner's control WebSocket git
// query handlers (`GetDiff` / `GetCommits` / `GetWorkspaceStatus` etc.).

export function parseDiffFiles(
  numstat: string,
  fullDiff: string,
): Array<{ file: string; additions: number; deletions: number; diff: string; isBinary: boolean }> {
  const patches = splitDiffByFile(fullDiff)
  const files: Array<{ file: string; additions: number; deletions: number; diff: string; isBinary: boolean }> = []

  for (const line of numstat.split('\n')) {
    if (!line.trim()) continue
    const parts = line.split('\t')
    if (parts.length < 3) continue
    const isBinary = parts[0] === '-' && parts[1] === '-'
    const add = isBinary ? 0 : parseInt(parts[0]) || 0
    const del = isBinary ? 0 : parseInt(parts[1]) || 0
    files.push({ file: parts[2], additions: add, deletions: del, diff: patches[parts[2]] ?? '', isBinary })
  }

  return files
}

export function splitDiffByFile(diff: string): Record<string, string> {
  const result: Record<string, string> = {}
  if (!diff.trim()) return result

  let currentPath: string | null = null
  const current: string[] = []

  for (const line of diff.split('\n')) {
    if (line.startsWith('diff --git ')) {
      flush()
      const parts = line.split(' ').filter(Boolean)
      currentPath = parts.length >= 4 && parts[3].startsWith('b/') ? parts[3].slice(2) : null
    }
    current.push(line)
  }
  flush()

  function flush() {
    if (currentPath && current.length > 0) result[currentPath] = current.join('\n') + '\n'
    current.length = 0
  }

  return result
}

export function parseCommits(
  log: string,
): Array<{ hash: string; shortHash: string; message: string; author: string; date: string; files: string[] }> {
  if (!log.trim()) return []
  return log
    .split('\n')
    .filter(Boolean)
    .map((line) => {
      const parts = line.split('\t')
      if (parts.length < 5) return null
      return {
        hash: parts[0],
        shortHash: parts[1],
        message: parts[2],
        author: parts[3],
        date: parts[4],
        files: [] as string[],
      }
    })
    .filter(Boolean) as Array<{
    hash: string
    shortHash: string
    message: string
    author: string
    date: string
    files: string[]
  }>
}

export function parseAheadBehind(output: string): [number, number] {
  const parts = output.trim().split('\t')
  if (parts.length === 2) {
    const behind = parseInt(parts[0]) || 0
    const ahead = parseInt(parts[1]) || 0
    return [ahead, behind]
  }
  return [0, 0]
}

export function parseNumstatTotal(numstat: string): { filesChanged: number; additions: number; deletions: number } {
  let filesChanged = 0
  let additions = 0
  let deletions = 0
  for (const line of numstat.split('\n')) {
    if (!line.trim()) continue
    const parts = line.split('\t')
    if (parts.length < 3) continue
    const isBinary = parts[0] === '-' && parts[1] === '-'
    additions += isBinary ? 0 : parseInt(parts[0]) || 0
    deletions += isBinary ? 0 : parseInt(parts[1]) || 0
    filesChanged++
  }
  return { filesChanged, additions, deletions }
}
