import * as signalR from "@microsoft/signalr"
import { runCommand } from "../system/process.js"

export class RunnerSignalRClient {
  private connection: signalR.HubConnection
  private readonly getWorkDir: (issueNumber: number) => string | null

  constructor(serverUrl: string, runnerId: string, getWorkDir: (issueNumber: number) => string | null) {
    const baseUrl = serverUrl.replace(/\/$/, "")
    this.getWorkDir = getWorkDir
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/runner?runnerId=${encodeURIComponent(runnerId)}`)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build()

    this.registerHandlers()
  }

  async start(): Promise<void> {
    await this.connection.start()
  }

  async stop(): Promise<void> {
    await this.connection.stop()
  }

  private registerHandlers(): void {
    this.connection.on("GetDiff", async (issueNumber: number) => {
      const workDir = this.getWorkDir(issueNumber)
      if (!workDir) return null

      const ac = new AbortController()
      const baseBranch = await detectBaseBranch(workDir, ac.signal)
      const head = `mo/issue-${issueNumber}`

      const branchExists = await git(workDir, ["rev-parse", "--verify", `refs/heads/${head}`], ac.signal)
      if (branchExists.exitCode !== 0) return null

      const [numstat, fullDiff, mergeBaseResult, aheadBehindResult, logResult] = await Promise.all([
        git(workDir, ["diff", `${baseBranch}...${head}`, "--numstat"], ac.signal),
        git(workDir, ["diff", `${baseBranch}...${head}`], ac.signal),
        git(workDir, ["merge-base", baseBranch, head], ac.signal),
        git(workDir, ["rev-list", "--left-right", "--count", `${baseBranch}...${head}`], ac.signal),
        git(workDir, ["log", `${baseBranch}...${head}`, "--format=%H"], ac.signal),
      ])

      const files = parseDiffFiles(numstat.stdout, fullDiff.stdout)
      const mergeBase = mergeBaseResult.exitCode === 0 ? mergeBaseResult.stdout.trim() : baseBranch
      const commitCount = logResult.exitCode === 0 ? logResult.stdout.trim().split("\n").filter(Boolean).length : 0
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)

      return {
        base: baseBranch,
        head,
        mergeBase,
        ahead,
        behind,
        commitCount,
        totalAdditions: files.reduce((s, f) => s + f.additions, 0),
        totalDeletions: files.reduce((s, f) => s + f.deletions, 0),
        files,
      }
    })

    this.connection.on("GetCommits", async (issueNumber: number) => {
      const workDir = this.getWorkDir(issueNumber)
      if (!workDir) return null

      const ac = new AbortController()
      const baseBranch = await detectBaseBranch(workDir, ac.signal)
      const head = `mo/issue-${issueNumber}`

      const [logResult, numstat, mergeBaseResult, aheadBehindResult] = await Promise.all([
        git(workDir, ["log", `${baseBranch}...${head}`, "--format=%H\t%h\t%s\t%an\t%ad", "--date=iso"], ac.signal),
        git(workDir, ["diff", `${baseBranch}...${head}`, "--numstat"], ac.signal),
        git(workDir, ["merge-base", baseBranch, head], ac.signal),
        git(workDir, ["rev-list", "--left-right", "--count", `${baseBranch}...${head}`], ac.signal),
      ])

      const commits = parseCommits(logResult.stdout)
      const mergeBase = mergeBaseResult.exitCode === 0 ? mergeBaseResult.stdout.trim() : baseBranch
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)
      const fileStats = parseNumstatTotal(numstat.stdout)

      return {
        base: baseBranch,
        head,
        mergeBase,
        ahead,
        behind,
        filesChanged: fileStats.filesChanged,
        totalAdditions: fileStats.additions,
        totalDeletions: fileStats.deletions,
        commits,
      }
    })

    this.connection.on("GetCommitDiff", async (_issueNumber: number, hash: string) => {
      const workDir = this.getWorkDir(_issueNumber)
      if (!workDir) return null

      const ac = new AbortController()
      const result = await git(workDir, ["show", "--format=", "--patch", hash], ac.signal)
      if (result.exitCode !== 0) return null
      return { diff: result.stdout }
    })

    this.connection.on("GetWorktreeStatus", async (issueNumber: number) => {
      const workDir = this.getWorkDir(issueNumber)
      if (!workDir) return { exists: false }

      const ac = new AbortController()
      const baseBranch = await detectBaseBranch(workDir, ac.signal)
      const branch = `mo/issue-${issueNumber}`

      const branchExists = await git(workDir, ["rev-parse", "--verify", `refs/heads/${branch}`], ac.signal)
      if (branchExists.exitCode !== 0) return { exists: false }

      const aheadBehindResult = await git(workDir, ["rev-list", "--left-right", "--count", `${baseBranch}...${branch}`], ac.signal)
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)
      const rebaseResult = await git(workDir, ["rebase", "--show-current-patch"], ac.signal)
      const rebaseInProgress = rebaseResult.exitCode === 0

      let conflictingFiles: string[] = []
      if (rebaseInProgress) {
        const statusResult = await git(workDir, ["diff", "--name-only", "--diff-filter=U"], ac.signal)
        conflictingFiles = statusResult.stdout.trim().split("\n").filter(Boolean)
      }

      return { exists: true, branch, baseBranch, ahead, behind, rebaseInProgress, conflictingFiles }
    })

    this.connection.on("GetFileContent", async (_issueNumber: number, path: string) => {
      const workDir = this.getWorkDir(_issueNumber)
      if (!workDir) return { base: null, head: null }

      const ac = new AbortController()
      const baseBranch = await detectBaseBranch(workDir, ac.signal)
      const head = `mo/issue-${_issueNumber}`

      const [baseResult, headResult] = await Promise.all([
        git(workDir, ["show", `${baseBranch}:${path}`], ac.signal),
        git(workDir, ["show", `${head}:${path}`], ac.signal),
      ])

      return {
        base: baseResult.exitCode === 0 ? baseResult.stdout : null,
        head: headResult.exitCode === 0 ? headResult.stdout : null,
      }
    })
  }
}

async function git(workDir: string, args: string[], signal: AbortSignal) {
  return runCommand("git", args, workDir, signal)
}

async function detectBaseBranch(workDir: string, signal: AbortSignal): Promise<string> {
  const result = await git(workDir, ["symbolic-ref", "--short", "HEAD"], signal)
  if (result.exitCode === 0) {
    const branch = result.stdout.trim()
    if (branch.startsWith("mo/issue-")) {
      const upstreamResult = await git(workDir, ["config", `branch.${branch}.merge`], signal)
      if (upstreamResult.exitCode === 0) {
        const merge = upstreamResult.stdout.trim().replace("refs/heads/", "")
        if (merge) return merge
      }
      return "main"
    }
    return branch
  }
  return "main"
}

function parseDiffFiles(numstat: string, fullDiff: string): Array<{ file: string; additions: number; deletions: number; diff: string; isBinary: boolean }> {
  const patches = splitDiffByFile(fullDiff)
  const files: Array<{ file: string; additions: number; deletions: number; diff: string; isBinary: boolean }> = []

  for (const line of numstat.split("\n")) {
    if (!line.trim()) continue
    const parts = line.split("\t")
    if (parts.length < 3) continue
    const isBinary = parts[0] === "-" && parts[1] === "-"
    const add = isBinary ? 0 : parseInt(parts[0]) || 0
    const del = isBinary ? 0 : parseInt(parts[1]) || 0
    files.push({ file: parts[2], additions: add, deletions: del, diff: patches[parts[2]] ?? "", isBinary })
  }

  return files
}

function splitDiffByFile(diff: string): Record<string, string> {
  const result: Record<string, string> = {}
  if (!diff.trim()) return result

  let currentPath: string | null = null
  const current: string[] = []

  for (const line of diff.split("\n")) {
    if (line.startsWith("diff --git ")) {
      flush()
      const parts = line.split(" ").filter(Boolean)
      currentPath = parts.length >= 4 && parts[3].startsWith("b/") ? parts[3].slice(2) : null
    }
    current.push(line)
  }
  flush()

  function flush() {
    if (currentPath && current.length > 0) result[currentPath] = current.join("\n") + "\n"
    current.length = 0
  }

  return result
}

function parseCommits(log: string): Array<{ hash: string; shortHash: string; message: string; author: string; date: string; files: string[] }> {
  if (!log.trim()) return []
  return log.split("\n").filter(Boolean).map(line => {
    const parts = line.split("\t")
    if (parts.length < 5) return null
    return { hash: parts[0], shortHash: parts[1], message: parts[2], author: parts[3], date: parts[4], files: [] as string[] }
  }).filter(Boolean) as Array<{ hash: string; shortHash: string; message: string; author: string; date: string; files: string[] }>
}

function parseAheadBehind(output: string): [number, number] {
  const parts = output.trim().split("\t")
  if (parts.length === 2) {
    const behind = parseInt(parts[0]) || 0
    const ahead = parseInt(parts[1]) || 0
    return [ahead, behind]
  }
  return [0, 0]
}

function parseNumstatTotal(numstat: string): { filesChanged: number; additions: number; deletions: number } {
  let filesChanged = 0
  let additions = 0
  let deletions = 0
  for (const line of numstat.split("\n")) {
    if (!line.trim()) continue
    const parts = line.split("\t")
    if (parts.length < 3) continue
    const isBinary = parts[0] === "-" && parts[1] === "-"
    additions += isBinary ? 0 : parseInt(parts[0]) || 0
    deletions += isBinary ? 0 : parseInt(parts[1]) || 0
    filesChanged++
  }
  return { filesChanged, additions, deletions }
}
