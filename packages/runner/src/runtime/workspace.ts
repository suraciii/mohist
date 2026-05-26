import { homedir, tmpdir } from "node:os"
import { join, resolve } from "node:path"
import type { JsonObject, WorkItem } from "../core/types.js"
import { ensureDir, exists, runCommand } from "../system/process.js"

export interface WorkspaceInfo {
  path: string
  branch?: string | null
  changeDir?: string | null
}

export class WorkspaceManager {
  constructor(private readonly runnerRoot = defaultRunnerRoot()) {}

  async ensure(work: WorkItem, signal: AbortSignal): Promise<WorkspaceInfo> {
    const variables = work.variables ?? {}
    const existing = stringAt(variables, ["workspace", "path"])
    if (existing) {
      await ensureDir(existing)
      return { path: existing, branch: stringAt(variables, ["workspace", "branch"]), changeDir: stringAt(variables, ["workspace", "changeDir"]) }
    }

    const projectPath = stringAt(variables, ["project", "path"])
    const issueNumber = numberAt(variables, ["issue", "number"])
    if (projectPath && issueNumber !== undefined) return await this.ensureIssueWorktree(variables, projectPath, issueNumber, signal)

    const fallback = resolve(join(this.runnerRoot, "fallback", work.workId))
    await ensureDir(fallback)
    return { path: fallback, changeDir: stringAt(variables, ["openspecChangeDir"]) }
  }

  private async ensureIssueWorktree(variables: JsonObject, projectPath: string, issueNumber: number, signal: AbortSignal): Promise<WorkspaceInfo> {
    const projectName = stringAt(variables, ["project", "name"]) ?? stringAt(variables, ["project", "id"]) ?? "project"
    const baseBranch = stringAt(variables, ["project", "baseBranch"]) ?? stringAt(variables, ["project", "defaultBranch"]) ?? "main"
    const branch = `mo/issue-${issueNumber}`
    const worktree = issueWorktreePath(this.runnerRoot, projectName, issueNumber)
    if (!exists(worktree)) {
      await ensureDir(worktree)
      const branchExists = await runCommand("git", ["rev-parse", "--verify", branch], projectPath, signal).then((result) => result.exitCode === 0)
      const args = branchExists ? ["worktree", "add", worktree, branch] : ["worktree", "add", "-b", branch, worktree, baseBranch]
      const result = await runCommand("git", args, projectPath, signal)
      if (result.exitCode !== 0) throw new Error(`git ${args.join(" ")} failed: ${result.stderr || result.stdout}`)
    }

    const changeDir = stringAt(variables, ["openspecChangeDir"])
    if (changeDir) await ensureDir(join(worktree, changeDir, "specs"))
    return { path: worktree, branch, changeDir: changeDir ? join(worktree, changeDir) : null }
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
