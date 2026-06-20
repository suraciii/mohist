import { existsSync } from "node:fs"
import { resolve, relative, isAbsolute } from "node:path"
import * as signalR from "@microsoft/signalr"
import { deleteDirectory, runCommand } from "../system/process.js"
import { WorkspaceManager } from "../runtime/workspace.js"
import type { WorkItem } from "../core/types.js"

export class RunnerSignalRClient {
  private connection: signalR.HubConnection
  private readonly workspaceManager: WorkspaceManager

  constructor(serverUrl: string, runnerId: string, private readonly runnerRoot: string, buildGitHash: string | null = null) {
    const baseUrl = serverUrl.replace(/\/$/, "")
    const params = new URLSearchParams()
    params.set("runnerId", runnerId)
    if (buildGitHash) params.set("buildGitHash", buildGitHash)
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/runner?${params.toString()}`)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build()
    this.workspaceManager = new WorkspaceManager(runnerRoot)

    this.registerHandlers()
  }

  async start(): Promise<void> {
    await this.connection.start()
  }

  async stop(): Promise<void> {
    await this.connection.stop()
  }

  private registerHandlers(): void {
    this.connection.on("MaterializeWorkspace", async (work: WorkItem) => {
      const ac = new AbortController()
      try {
        const info = await this.workspaceManager.materialize(work, ac.signal)
        return { ok: true, workspacePath: info.path, branch: info.branch ?? null, changeDir: info.changeDir ?? null }
      } catch (error) {
        return { ok: false, message: error instanceof Error ? error.message : String(error) }
      }
    })

    this.connection.on("GetDiff", async (query: WorkspaceQuery) => {
      const workspace = resolveWorkspaceQuery(query)
      if (!workspace) return null
      const ac = new AbortController()
      if (!await isGitWorkTree(workspace.workDir, ac.signal)) return null

      const branchExists = await git(workspace.workDir, ["rev-parse", "--verify", `refs/heads/${workspace.head}`], ac.signal)
      if (branchExists.exitCode !== 0) return null

      const [numstat, fullDiff, mergeBaseResult, aheadBehindResult, logResult] = await Promise.all([
        git(workspace.workDir, ["diff", `${workspace.baseBranch}...${workspace.head}`, "--numstat"], ac.signal),
        git(workspace.workDir, ["diff", `${workspace.baseBranch}...${workspace.head}`], ac.signal),
        git(workspace.workDir, ["merge-base", workspace.baseBranch, workspace.head], ac.signal),
        git(workspace.workDir, ["rev-list", "--left-right", "--count", `${workspace.baseBranch}...${workspace.head}`], ac.signal),
        git(workspace.workDir, ["log", `${workspace.baseBranch}...${workspace.head}`, "--format=%H"], ac.signal),
      ])

      const files = parseDiffFiles(numstat.stdout, fullDiff.stdout)
      const mergeBase = mergeBaseResult.exitCode === 0 ? mergeBaseResult.stdout.trim() : workspace.baseBranch
      const commitCount = logResult.exitCode === 0 ? logResult.stdout.trim().split("\n").filter(Boolean).length : 0
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)

      return {
        base: workspace.baseBranch,
        head: workspace.head,
        mergeBase,
        ahead,
        behind,
        commitCount,
        totalAdditions: files.reduce((s, f) => s + f.additions, 0),
        totalDeletions: files.reduce((s, f) => s + f.deletions, 0),
        files,
      }
    })

    this.connection.on("GetCommits", async (query: WorkspaceQuery) => {
      const workspace = resolveWorkspaceQuery(query)
      if (!workspace) return null
      const ac = new AbortController()
      if (!await isGitWorkTree(workspace.workDir, ac.signal)) return null

      const [logResult, numstat, mergeBaseResult, aheadBehindResult] = await Promise.all([
        git(workspace.workDir, ["log", `${workspace.baseBranch}...${workspace.head}`, "--format=%H\t%h\t%s\t%an\t%ad", "--date=iso"], ac.signal),
        git(workspace.workDir, ["diff", `${workspace.baseBranch}...${workspace.head}`, "--numstat"], ac.signal),
        git(workspace.workDir, ["merge-base", workspace.baseBranch, workspace.head], ac.signal),
        git(workspace.workDir, ["rev-list", "--left-right", "--count", `${workspace.baseBranch}...${workspace.head}`], ac.signal),
      ])

      const commits = parseCommits(logResult.stdout)
      const mergeBase = mergeBaseResult.exitCode === 0 ? mergeBaseResult.stdout.trim() : workspace.baseBranch
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)
      const fileStats = parseNumstatTotal(numstat.stdout)

      return {
        base: workspace.baseBranch,
        head: workspace.head,
        mergeBase,
        ahead,
        behind,
        filesChanged: fileStats.filesChanged,
        totalAdditions: fileStats.additions,
        totalDeletions: fileStats.deletions,
        commits,
      }
    })

    this.connection.on("GetCommitDiff", async (query: WorkspaceQuery, hash: string) => {
      const workspace = resolveWorkspaceQuery(query)
      if (!workspace) return null

      const ac = new AbortController()
      if (!await isGitWorkTree(workspace.workDir, ac.signal)) return null
      const result = await git(workspace.workDir, ["show", "--format=", "--patch", hash], ac.signal)
      if (result.exitCode !== 0) return null
      return { diff: result.stdout }
    })

    this.connection.on("GetWorkspaceStatus", async (query: WorkspaceQuery) => {
      const workspace = resolveWorkspaceQuery(query)
      if (!workspace) return { exists: false }

      const ac = new AbortController()
      if (!await isGitWorkTree(workspace.workDir, ac.signal)) return { exists: false }

      const branchExists = await git(workspace.workDir, ["rev-parse", "--verify", `refs/heads/${workspace.head}`], ac.signal)
      if (branchExists.exitCode !== 0) return { exists: false }

      const fetchResult = await git(workspace.workDir, ["fetch", "origin", workspace.baseBranch], ac.signal)
      if (fetchResult.exitCode !== 0) return { exists: false, reason: "fetch_failed" }
      const remoteRef = `origin/${workspace.baseBranch}`
      const aheadBehindResult = await git(workspace.workDir, ["rev-list", "--left-right", "--count", `${remoteRef}...${workspace.head}`], ac.signal)
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)
      const rebaseResult = await git(workspace.workDir, ["rebase", "--show-current-patch"], ac.signal)
      const rebaseInProgress = rebaseResult.exitCode === 0

      let conflictingFiles: string[] = []
      if (rebaseInProgress) {
        const statusResult = await git(workspace.workDir, ["diff", "--name-only", "--diff-filter=U"], ac.signal)
        conflictingFiles = statusResult.stdout.trim().split("\n").filter(Boolean)
      }

      return { exists: true, branch: workspace.head, baseBranch: workspace.baseBranch, ahead, behind, rebaseInProgress, conflictingFiles }
    })

    this.connection.on("GetFileContent", async (query: WorkspaceQuery, path: string) => {
      const workspace = resolveWorkspaceQuery(query)
      if (!workspace) return { base: null, head: null }

      const ac = new AbortController()
      if (!await isGitWorkTree(workspace.workDir, ac.signal)) return { base: null, head: null }

      const [baseResult, headResult] = await Promise.all([
        git(workspace.workDir, ["show", `${workspace.baseBranch}:${path}`], ac.signal),
        git(workspace.workDir, ["show", `${workspace.head}:${path}`], ac.signal),
      ])

      return {
        base: baseResult.exitCode === 0 ? baseResult.stdout : null,
        head: headResult.exitCode === 0 ? headResult.stdout : null,
      }
    })

    this.connection.on("RemoveWorkspace", async (query: WorkspaceQuery) => {
      if (!query?.workspacePath) return removal(false, "missing", query?.workspacePath ?? null, "workspace_missing", "Workspace already removed")
      const workspacePath = resolve(query.workspacePath)
      if (!existsSync(workspacePath)) return removal(false, "missing", workspacePath, "workspace_missing", "Workspace already removed")
      if (!isUnderRunnerRoot(this.runnerRoot, workspacePath)) {
        return removal(false, "failed", workspacePath, "workspace_cleanup_refused", "Workspace path is outside the runner-managed root")
      }
      try {
        await deleteDirectory(workspacePath)
        return removal(true, "removed", workspacePath, null, "Workspace removed")
      } catch (error) {
        return removal(false, "failed", workspacePath, "workspace_cleanup_failed", error instanceof Error ? error.message : String(error))
      }
    })
  }
}

async function git(workDir: string, args: string[], signal: AbortSignal) {
  return runCommand("git", args, workDir, signal)
}

export interface WorkspaceQuery {
  issueNumber?: number
  workspacePath?: string | null
  branch?: string | null
  baseBranch?: string | null
}

export function resolveWorkspaceQuery(query: WorkspaceQuery | null | undefined): { workDir: string; baseBranch: string; head: string } | null {
  if (!query?.workspacePath || !query.baseBranch) return null
  // The legacy `mo/issue-{N}` worktree branch is no longer created by the
  // runner. Callers MUST supply an explicit `branch` (the workspace's HEAD
  // ref, e.g. `mohist/run-${workflowRunId}`). Returning null forces the
  // server review APIs to surface `branch_missing` instead of a phantom
  // `mo/issue-{N}` ref that would never resolve.
  const head = query.branch ?? null
  if (!head) return null
  return { workDir: query.workspacePath, baseBranch: query.baseBranch, head }
}

async function isGitWorkTree(workDir: string, signal: AbortSignal): Promise<boolean> {
  if (!existsSync(workDir)) return false
  const result = await git(workDir, ["rev-parse", "--is-inside-work-tree"], signal)
  return result.exitCode === 0 && result.stdout.trim() === "true"
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

export function isUnderRunnerRoot(root: string, candidate: string): boolean {
  const rootPath = resolve(root)
  const target = resolve(candidate)
  const rel = relative(rootPath, target)
  return rel === "" || (!rel.startsWith("..") && !isAbsolute(rel))
}

function removal(removed: boolean, status: string, path: string | null, reason: string | null, message: string) {
  return { removed, status, path, reason, message }
}
