import { existsSync as defaultExistsSync } from "node:fs"
import * as signalR from "@microsoft/signalr"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../actions/git.js"
import { runCommand as defaultRunCommand, type CommandLineOptions } from "../system/process.js"
import { currentRunnerResources } from "../system/filesystem.js"
import {
  resolveWorkspaceQuery,
  type WorkspaceQuery,
  hasCompleteWorkspaceIdentity,
} from "../runtime/workspace-query.js"
import { issueWorkspacePath, validateWorkspaceIdentity, type IssueWorkspaceMarker } from "../runtime/workspace.js"
import {
  parseAheadBehind,
  parseCommits,
  parseDiffFiles,
  parseNumstatTotal,
} from "./git-parsers.js"

export interface WorkspaceGitHandlerDeps {
  resolveQuery: typeof resolveWorkspaceQuery
  runnerRoot?: string
  runCommand?: typeof defaultRunCommand
  pathExists?: typeof defaultExistsSync
  allowUnverifiedWorkspaceQueriesForTest?: boolean
}

export async function git(workDir: string, args: string[], signal: AbortSignal, options?: CommandLineOptions) {
  const runner = currentRunnerResources()?.signalRGitRunner ?? defaultRunCommand
  return runner("git", args, workDir, signal, undefined, options)
}

export async function isGitWorkTree(workDir: string, signal: AbortSignal): Promise<boolean> {
  const checker = currentRunnerResources()?.signalRExistsChecker ?? defaultExistsSync
  if (!checker(workDir)) return false
  const result = await git(workDir, ["rev-parse", "--is-inside-work-tree"], signal)
  return result.exitCode === 0 && result.stdout.trim() === "true"
}

export function registerWorkspaceGitHandlers(
  conn: signalR.HubConnection,
  deps: WorkspaceGitHandlerDeps,
): void {
  const resolveQuery = deps.resolveQuery

  async function runGit(workDir: string, args: string[], signal: AbortSignal, options?: CommandLineOptions) {
    const runner = deps.runCommand ?? currentRunnerResources()?.signalRGitRunner ?? defaultRunCommand
    return runner("git", args, workDir, signal, undefined, options)
  }

  async function isWorkTree(workDir: string, signal: AbortSignal): Promise<boolean> {
    const checker = deps.pathExists ?? currentRunnerResources()?.signalRExistsChecker ?? defaultExistsSync
    if (!checker(workDir)) return false
    const result = await runGit(workDir, ["rev-parse", "--is-inside-work-tree"], signal)
    return result.exitCode === 0 && result.stdout.trim() === "true"
  }

  async function validateIdentity(query: WorkspaceQuery, signal: AbortSignal): Promise<boolean> {
    if (!hasCompleteWorkspaceIdentity(query)) return deps.allowUnverifiedWorkspaceQueriesForTest === true
    if (deps.allowUnverifiedWorkspaceQueriesForTest) return true
    if (!deps.runnerRoot) return false
    if (query.workspacePath !== issueWorkspacePath(deps.runnerRoot, query.workflowRunId)) return false
    const expected: IssueWorkspaceMarker = {
      workflowRunId: query.workflowRunId,
      runBranch: query.branch,
    }
    try {
      await validateWorkspaceIdentity(query.workspacePath, expected, query.gitUrl, signal, null, deps.runnerRoot)
      return true
    } catch {
      return false
    }
  }

  conn.on("GetDiff", async (query: WorkspaceQuery) => {
    const workspace = resolveQuery(query)
    if (!workspace) return null
    const ac = new AbortController()
    if (!await validateIdentity(query, ac.signal)) return null
    if (!await isWorkTree(workspace.workDir, ac.signal)) return null

    const branchExists = await runGit(workspace.workDir, ["rev-parse", "--verify", `refs/heads/${workspace.head}`], ac.signal)
    if (branchExists.exitCode !== 0) return null

    const [numstat, fullDiff, mergeBaseResult, aheadBehindResult, logResult] = await Promise.all([
      runGit(workspace.workDir, ["diff", `${workspace.baseBranch}...${workspace.head}`, "--numstat"], ac.signal),
      runGit(workspace.workDir, ["diff", `${workspace.baseBranch}...${workspace.head}`], ac.signal),
      runGit(workspace.workDir, ["merge-base", workspace.baseBranch, workspace.head], ac.signal),
      runGit(workspace.workDir, ["rev-list", "--left-right", "--count", `${workspace.baseBranch}...${workspace.head}`], ac.signal),
      runGit(workspace.workDir, ["log", `${workspace.baseBranch}...${workspace.head}`, "--format=%H"], ac.signal),
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

  conn.on("GetCommits", async (query: WorkspaceQuery) => {
    const workspace = resolveQuery(query)
    if (!workspace) return null
    const ac = new AbortController()
    if (!await validateIdentity(query, ac.signal)) return null
    if (!await isWorkTree(workspace.workDir, ac.signal)) return null

    const [logResult, numstat, mergeBaseResult, aheadBehindResult] = await Promise.all([
      runGit(workspace.workDir, ["log", `${workspace.baseBranch}...${workspace.head}`, "--format=%H\t%h\t%s\t%an\t%ad", "--date=iso"], ac.signal),
      runGit(workspace.workDir, ["diff", `${workspace.baseBranch}...${workspace.head}`, "--numstat"], ac.signal),
      runGit(workspace.workDir, ["merge-base", workspace.baseBranch, workspace.head], ac.signal),
      runGit(workspace.workDir, ["rev-list", "--left-right", "--count", `${workspace.baseBranch}...${workspace.head}`], ac.signal),
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

  conn.on("GetCommitDiff", async (query: WorkspaceQuery, hash: string) => {
    const workspace = resolveQuery(query)
    if (!workspace) return null

    const ac = new AbortController()
    if (!await validateIdentity(query, ac.signal)) return null
    if (!await isWorkTree(workspace.workDir, ac.signal)) return null
    const result = await runGit(workspace.workDir, ["show", "--format=", "--patch", hash], ac.signal)
    if (result.exitCode !== 0) return null
    return { diff: result.stdout }
  })

  conn.on("GetWorkspaceStatus", async (query: WorkspaceQuery) => {
    const workspace = resolveQuery(query)
    if (!workspace) return { exists: false }

    const ac = new AbortController()
    if (!await validateIdentity(query, ac.signal)) return { exists: false, reason: "workspace_identity_mismatch" }
    if (!await isWorkTree(workspace.workDir, ac.signal)) return { exists: false }

    const branchExists = await runGit(workspace.workDir, ["rev-parse", "--verify", `refs/heads/${workspace.head}`], ac.signal)
    if (branchExists.exitCode !== 0) return { exists: false }

    const rebaseResult = await runGit(workspace.workDir, ["rebase", "--show-current-patch"], ac.signal)
    const rebaseInProgress = rebaseResult.exitCode === 0

    let conflictingFiles: string[] = []
    if (rebaseInProgress) {
      const statusResult = await runGit(workspace.workDir, ["diff", "--name-only", "--diff-filter=U"], ac.signal)
      conflictingFiles = statusResult.stdout.trim().split("\n").filter(Boolean)
    }

    const baseStatus = { exists: true, branch: workspace.head, baseBranch: workspace.baseBranch, rebaseInProgress, conflictingFiles }

    const fetchResult = await runGit(workspace.workDir, ["fetch", "origin", workspace.baseBranch], ac.signal, { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS })
    if (fetchResult.exitCode !== 0) return { ...baseStatus, reason: "fetch_failed" }
    if (rebaseInProgress) return { ...baseStatus, reason: "rebase_in_progress" }

    const remoteRef = `origin/${workspace.baseBranch}`
    const aheadBehindResult = await runGit(workspace.workDir, ["rev-list", "--left-right", "--count", `${remoteRef}...${workspace.head}`], ac.signal)
    const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)

    return { ...baseStatus, ahead, behind }
  })

  conn.on("GetFileContent", async (query: WorkspaceQuery, path: string) => {
    const workspace = resolveQuery(query)
    if (!workspace) return { base: null, head: null }

    const ac = new AbortController()
    if (!await validateIdentity(query, ac.signal)) return { base: null, head: null }
    if (!await isWorkTree(workspace.workDir, ac.signal)) return { base: null, head: null }

    const [baseResult, headResult] = await Promise.all([
      runGit(workspace.workDir, ["show", `${workspace.baseBranch}:${path}`], ac.signal),
      runGit(workspace.workDir, ["show", `${workspace.head}:${path}`], ac.signal),
    ])

    return {
      base: baseResult.exitCode === 0 ? baseResult.stdout : null,
      head: headResult.exitCode === 0 ? headResult.stdout : null,
    }
  })
}
