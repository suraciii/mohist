import { homedir, tmpdir } from "node:os"
import { join, resolve } from "node:path"
import type { JsonObject, WorkItem } from "../core/types.js"
import { deleteDirectory, ensureDir, exists, readText, runCommand, writeText } from "../system/process.js"

export interface WorkspaceInfo {
  path: string
  branch?: string | null
  changeDir?: string | null
}

export class WorkspaceManager {
  constructor(private readonly runnerRoot = defaultRunnerRoot()) {}

  async ensure(work: WorkItem, signal: AbortSignal): Promise<WorkspaceInfo> {
    const variables = work.variables ?? {}
    const gitUrl = stringAt(variables, ["repository", "gitUrl"])
    const baseBranch = stringAt(variables, ["repository", "baseBranch"])
    const issueNumber = numberAt(variables, ["issue", "number"])

    if (!gitUrl || issueNumber === undefined) {
      const fallback = resolve(join(this.runnerRoot, "fallback", work.workId))
      await ensureDir(fallback)
      return { path: fallback, changeDir: stringAt(variables, ["openspecChangeDir"]) }
    }

    const projectId = stringAt(variables, ["project", "id"]) ?? "project"
    const projectName = stringAt(variables, ["project", "name"]) ?? projectId
    const repoName = stringAt(variables, ["repository", "name"]) ?? "repo"
    const effectiveBaseBranch = baseBranch ?? "main"
    const runId = stringAt(variables, ["mohist", "runId"]) ?? work.workflowRunId
    const runBranch = runBranchName(runId)

    const cachePath = resolve(join(this.runnerRoot, "repos", slug(projectId), slug(repoName)))
    await this.ensureCache(cachePath, gitUrl, signal)
    await this.resolveBranch(cachePath, effectiveBaseBranch, signal)

    // workspace.path is a runtime fact supplied by the server. The runner
    // materializes the workspace at that path; it does not short-circuit
    // the cache/clone work that prepares a real working tree.
    const suppliedPath = stringAt(variables, ["workspace", "path"])
    const workspacePath = suppliedPath
      ? resolve(suppliedPath)
      : issueWorkspacePath(this.runnerRoot, projectName, issueNumber)
    const marker = issueWorkspaceMarker(variables)
    await this.ensureFreshWorkspace(cachePath, workspacePath, effectiveBaseBranch, runBranch, gitUrl, marker, signal)

    const changeDir = stringAt(variables, ["openspecChangeDir"])
    if (changeDir) await ensureDir(join(workspacePath, changeDir, "specs"))
    await writeText(markerPath(workspacePath), JSON.stringify(marker, null, 2))
    return { path: workspacePath, branch: runBranch, changeDir: changeDir ? join(workspacePath, changeDir) : null }
  }

  private async ensureCache(cachePath: string, gitUrl: string, signal: AbortSignal) {
    if (exists(cachePath)) {
      const result = await runCommand("git", ["-C", cachePath, "remote", "get-url", "origin"], ".", signal)
      if (result.exitCode === 0 && result.stdout.trim() === gitUrl) {
        const fetchResult = await runCommand("git", ["-C", cachePath, "fetch", "origin"], ".", signal)
        if (fetchResult.exitCode === 0) return
      }
      await deleteDirectory(cachePath)
    }
    await ensureDir(join(cachePath, ".."))
    const result = await runCommand("git", ["clone", "--bare", gitUrl, cachePath], ".", signal)
    if (result.exitCode !== 0) throw new Error(`git clone failed for ${gitUrl}: ${result.stderr || result.stdout}`)
  }

  private async resolveBranch(cachePath: string, baseBranch: string, signal: AbortSignal) {
    const local = await runCommand("git", ["-C", cachePath, "rev-parse", "--verify", `refs/heads/${baseBranch}`], ".", signal)
    if (local.exitCode === 0) return
    const remote = await runCommand("git", ["-C", cachePath, "rev-parse", "--verify", `refs/remotes/origin/${baseBranch}`], ".", signal)
    if (remote.exitCode === 0) return
    throw new Error(`Configured base branch '${baseBranch}' cannot be resolved from repository gitUrl.`)
  }

  private async ensureFreshWorkspace(cachePath: string, workspacePath: string, baseBranch: string, runBranch: string, gitUrl: string, marker: IssueWorkspaceMarker, signal: AbortSignal) {
    if (exists(workspacePath) && !await hasSameMarker(workspacePath, marker)) {
      await deleteDirectory(workspacePath)
    }

    if (!exists(workspacePath)) {
      await ensureDir(join(workspacePath, ".."))
      const result = await runCommand("git", ["clone", "--shared", "--branch", baseBranch, "--single-branch", cachePath, workspacePath], ".", signal)
      if (result.exitCode !== 0) throw new Error(`git clone failed for workspace: ${result.stderr || result.stdout}`)
    }

    // Always (re)create the per-run branch on the workspace clone so the head
    // ref is stable for merge, rebase, and review APIs.
    await this.ensureRunBranch(workspacePath, baseBranch, runBranch, signal)

    // Reset the workspace's `origin` to the original gitUrl so push/PR-style
    // operations and human inspection see the upstream source, not the bare
    // local cache.
    const remote = await runCommand("git", ["-C", workspacePath, "remote", "get-url", "origin"], ".", signal)
    if (remote.exitCode !== 0 || remote.stdout.trim() !== gitUrl) {
      const setUrl = await runCommand("git", ["-C", workspacePath, "remote", "set-url", "origin", gitUrl], workspacePath, signal)
      if (setUrl.exitCode !== 0) throw new Error(`git remote set-url failed: ${setUrl.stderr || setUrl.stdout}`)
    }
  }

  private async ensureRunBranch(workspacePath: string, baseBranch: string, runBranch: string, signal: AbortSignal) {
    const current = await runCommand("git", ["-C", workspacePath, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal)
    if (current.exitCode === 0 && current.stdout.trim() === runBranch) return

    const existing = await runCommand("git", ["-C", workspacePath, "rev-parse", "--verify", `refs/heads/${runBranch}`], ".", signal)
    if (existing.exitCode === 0) {
      const checkout = await runCommand("git", ["-C", workspacePath, "checkout", runBranch], workspacePath, signal)
      if (checkout.exitCode !== 0) throw new Error(`git checkout ${runBranch} failed: ${checkout.stderr || checkout.stdout}`)
      return
    }

    const create = await runCommand("git", ["-C", workspacePath, "checkout", "-b", runBranch], workspacePath, signal)
    if (create.exitCode !== 0) throw new Error(`git checkout -b ${runBranch} failed: ${create.stderr || create.stdout}`)
    // reset --hard origin/<baseBranch> only if the initial checkout landed on
    // a stale local branch; otherwise stay on the freshly-created branch.
    const headSha = await runCommand("git", ["-C", workspacePath, "rev-parse", "HEAD"], ".", signal)
    const baseSha = await runCommand("git", ["-C", workspacePath, "rev-parse", "--verify", `refs/heads/${baseBranch}`], ".", signal)
    if (headSha.exitCode === 0 && baseSha.exitCode === 0 && headSha.stdout.trim() !== baseSha.stdout.trim()) {
      const reset = await runCommand("git", ["-C", workspacePath, "reset", "--hard", baseBranch], workspacePath, signal)
      if (reset.exitCode !== 0) throw new Error(`git reset --hard ${baseBranch} failed: ${reset.stderr || reset.stdout}`)
    }
  }
}

export function defaultRunnerRoot() {
  return process.env.MOHIST_RUNNER_ROOT ?? process.env.MOHIST_WORKSPACE_ROOT ?? join(homedir(), ".mohist", "projects")
}

export function runnerVariables() {
  return { os: process.platform, hostname: process.env.COMPUTERNAME ?? process.env.HOSTNAME ?? "unknown", temp: tmpdir() }
}

function issueWorkspacePath(runnerRoot: string, projectName: string, issueNumber: number) {
  return resolve(join(runnerRoot, slug(projectName), "workspaces", `issue-${issueNumber}`))
}

function runBranchName(runId: string | null | undefined) {
  const safe = (runId ?? "").replace(/[^A-Za-z0-9_-]/g, "")
  return safe ? `mohist/run-${safe}` : "mohist/run"
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

async function hasSameMarker(workspacePath: string, expected: IssueWorkspaceMarker) {
  const path = markerPath(workspacePath)
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

function markerPath(workspacePath: string) {
  return join(workspacePath, ".mohist", "workspace.json")
}

function slug(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "") || "project"
}

export { slug as slugify }

function stringAt(value: JsonObject | undefined, path: string[]) {
  const found = at(value, path)
  return typeof found === "string" ? found : undefined
}

function numberAt(value: JsonObject | undefined, path: string[]) {
  const found = at(value, path)
  return typeof found === "number" ? found : undefined
}

function at(value: JsonObject | undefined, path: string[]) {
  if (!value) return undefined
  return path.reduce<unknown>((current, part) => {
    if (typeof current !== "object" || current === null || Array.isArray(current)) return undefined
    return (current as JsonObject)[part]
  }, value)
}
