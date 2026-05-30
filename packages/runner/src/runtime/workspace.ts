import { homedir, tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { readdirSync } from "node:fs"
import type { JsonObject, WorkItem } from "../core/types.js"
import { deleteDirectory, ensureDir, exists, readText, runCommand, writeText } from "../system/process.js"

export interface WorkspaceInfo {
  path: string
  branch?: string | null
  changeDir?: string | null
}

export class WorkspaceManager {
  constructor(private readonly runnerRoot = defaultRunnerRoot()) {}

  getExistingWorkDir(issueNumber: number): string | null {
    const target = `issue-${issueNumber}`
    try {
      for (const project of readdirSync(this.runnerRoot, { withFileTypes: true })) {
        if (!project.isDirectory()) continue
        const worktreesDir = join(this.runnerRoot, project.name, "worktrees")
        if (!exists(worktreesDir)) continue
        const candidate = join(worktreesDir, target)
        if (exists(candidate)) return resolve(candidate)
      }
    } catch {}
    return null
  }

  async ensure(work: WorkItem, signal: AbortSignal): Promise<WorkspaceInfo> {
    const variables = work.variables ?? {}
    const existing = stringAt(variables, ["workspace", "path"])
    if (existing) {
      await ensureDir(existing)
      return { path: existing, branch: stringAt(variables, ["workspace", "branch"]), changeDir: stringAt(variables, ["workspace", "changeDir"]) }
    }

    const repoPath = stringAt(variables, ["repository", "path"])
    const repoRemote = stringAt(variables, ["repository", "remote"])
    const projectPath = stringAt(variables, ["project", "path"])
    const issueNumber = numberAt(variables, ["issue", "number"])

    const resolvedPath = repoPath ?? projectPath
    const resolvedRemote = repoRemote

    if (resolvedPath && issueNumber !== undefined) {
      return await this.ensureIssueWorktree(variables, resolvedPath, issueNumber, signal)
    }

    if (resolvedRemote && issueNumber !== undefined) {
      const projectName = stringAt(variables, ["project", "name"]) ?? stringAt(variables, ["project", "id"]) ?? "project"
      const repoName = stringAt(variables, ["repository", "name"]) ?? "repo"
      const clonePath = resolve(join(this.runnerRoot, "repos", slug(projectName), slug(repoName)))
      await this.ensureCloned(clonePath, resolvedRemote, signal)
      return await this.ensureIssueWorktree(variables, clonePath, issueNumber, signal)
    }

    const fallback = resolve(join(this.runnerRoot, "fallback", work.workId))
    await ensureDir(fallback)
    return { path: fallback, changeDir: stringAt(variables, ["openspecChangeDir"]) }
  }

  private async ensureCloned(clonePath: string, remote: string, signal: AbortSignal) {
    if (exists(clonePath)) {
      const result = await runCommand("git", ["-C", clonePath, "remote", "get-url", "origin"], ".", signal)
      if (result.exitCode === 0 && result.stdout.trim() === remote) return
      await deleteDirectory(clonePath)
    }
    await ensureDir(join(clonePath, ".."))
    const result = await runCommand("git", ["clone", "--bare", remote, clonePath], ".", signal)
    if (result.exitCode !== 0) throw new Error(`git clone failed: ${result.stderr || result.stdout}`)
  }

  private async ensureIssueWorktree(variables: JsonObject, projectPath: string, issueNumber: number, signal: AbortSignal): Promise<WorkspaceInfo> {
    const projectName = stringAt(variables, ["project", "name"]) ?? stringAt(variables, ["project", "id"]) ?? "project"
    const baseBranch = stringAt(variables, ["repository", "baseBranch"]) ?? stringAt(variables, ["project", "baseBranch"]) ?? stringAt(variables, ["project", "defaultBranch"]) ?? "main"
    const branch = `mo/issue-${issueNumber}`
    const worktree = issueWorktreePath(this.runnerRoot, projectName, issueNumber)
    const marker = issueWorkspaceMarker(variables)
    await this.ensureFreshWorktree(projectPath, worktree, branch, baseBranch, marker, signal)

    const changeDir = stringAt(variables, ["openspecChangeDir"])
    if (changeDir) await ensureDir(join(worktree, changeDir, "specs"))
    await writeText(markerPath(worktree), JSON.stringify(marker, null, 2))
    return { path: worktree, branch, changeDir: changeDir ? join(worktree, changeDir) : null }
  }

  private async ensureFreshWorktree(projectPath: string, worktree: string, branch: string, baseBranch: string, marker: IssueWorkspaceMarker, signal: AbortSignal) {
    if (exists(worktree) && !await hasSameMarker(worktree, marker)) {
      const removed = await runCommand("git", ["worktree", "remove", "--force", worktree], projectPath, signal)
      if (removed.exitCode !== 0) await deleteDirectory(worktree)
    }

    if (!exists(worktree)) {
      await ensureBranchAvailableForFreshWorktree(projectPath, branch, signal)
      const result = await runCommand("git", ["worktree", "add", "-b", branch, worktree, baseBranch], projectPath, signal)
      if (result.exitCode !== 0) throw new Error(`git worktree add -b ${branch} ${worktree} ${baseBranch} failed: ${result.stderr || result.stdout}`)
    }
  }
}

export function defaultRunnerRoot() {
  return process.env.MOHIST_RUNNER_ROOT ?? process.env.MOHIST_WORKSPACE_ROOT ?? join(homedir(), ".mohist", "projects")
}

export function runnerVariables() {
  return { os: process.platform, hostname: process.env.COMPUTERNAME ?? process.env.HOSTNAME ?? "unknown", temp: tmpdir() }
}

function issueWorktreePath(runnerRoot: string, projectName: string, issueNumber: number) {
  return resolve(join(runnerRoot, slug(projectName), "worktrees", `issue-${issueNumber}`))
}

interface IssueWorkspaceMarker {
  issueId: string | null
  issueNumber: number
  workflowRunId: string | null
}

function issueWorkspaceMarker(variables: JsonObject): IssueWorkspaceMarker {
  return {
    issueId: stringAt(variables, ["issue", "id"]) ?? null,
    issueNumber: numberAt(variables, ["issue", "number"]) ?? 0,
    workflowRunId: stringAt(variables, ["mohist", "runId"]) ?? null,
  }
}

async function hasSameMarker(worktree: string, expected: IssueWorkspaceMarker) {
  const path = markerPath(worktree)
  if (!exists(path)) return false
  try {
    const actual = JSON.parse(await readText(path)) as Partial<IssueWorkspaceMarker>
    return actual.issueId === expected.issueId
      && actual.issueNumber === expected.issueNumber
      && actual.workflowRunId === expected.workflowRunId
  } catch {
    return false
  }
}

function markerPath(worktree: string) {
  return join(worktree, ".mohist", "workspace.json")
}

async function ensureBranchAvailableForFreshWorktree(projectPath: string, branch: string, signal: AbortSignal) {
  const branchExists = await runCommand("git", ["rev-parse", "--verify", branch], projectPath, signal).then((result) => result.exitCode === 0)
  if (!branchExists) return

  const deleted = await runCommand("git", ["branch", "-D", branch], projectPath, signal)
  if (deleted.exitCode !== 0) throw new Error(`git branch -D ${branch} failed: ${deleted.stderr || deleted.stdout}`)
}

function slug(value: string) {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "") || "project"
}

function stringAt(value: JsonObject, path: string[]) {
  const found = at(value, path)
  return typeof found === "string" ? found : undefined
}

function numberAt(value: JsonObject, path: string[]) {
  const found = at(value, path)
  return typeof found === "number" ? found : undefined
}

function at(value: JsonObject, path: string[]) {
  return path.reduce<unknown>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return (current as JsonObject)[part]
  }, value)
}
