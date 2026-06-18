import { randomUUID } from "node:crypto"
import { readdir } from "node:fs/promises"
import { homedir, tmpdir } from "node:os"
import { join, resolve } from "node:path"
import type { JsonObject, WorkItem } from "../core/types.js"
import { deleteDirectory, ensureDir, exists, readText, runCommand, writeText } from "../system/process.js"

export interface WorkspaceInfo {
  path: string
  branch?: string | null
  changeDir?: string | null
}

export interface LandingWorkspaceInfo {
  path: string
  runId: string
  runBranch: string
  baseBranch: string
  gitUrl: string
}

export class WorkspaceManager {
  constructor(private readonly runnerRoot = defaultRunnerRoot()) {}

  async ensure(work: WorkItem, signal: AbortSignal): Promise<WorkspaceInfo> {
    const variables = work.variables ?? {}
    const gitUrl = stringAt(variables, ["repository", "gitUrl"])
    const baseBranch = stringAt(variables, ["repository", "baseBranch"])
    const issueNumber = numberAt(variables, ["issue", "number"])

    if (!gitUrl || issueNumber === undefined) {
      throw new Error(`Workspace requires repository.gitUrl and issue.number in variables. Got gitUrl=${gitUrl ?? "null"}, issueNumber=${issueNumber ?? "undefined"}`)
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

    // Prune any landing workspaces left behind by a previous, possibly
    // crashed, run. Each landing is runId-scoped (and uuid-disambiguated),
    // so removing them cannot affect a different run and cannot reach into
    // the workflow workspace's object store — landing clones are isolated
    // `--shared` alternates clones of the workflow workspace.
    await this.pruneLandingWorkspaces(variables, runId, signal)

    const changeDir = stringAt(variables, ["openspecChangeDir"])
    if (changeDir) await ensureDir(join(workspacePath, changeDir, "specs"))
    await writeText(markerPath(workspacePath), JSON.stringify(marker, null, 2))
    return { path: workspacePath, branch: runBranch, changeDir: changeDir ? join(workspacePath, changeDir) : null }
  }

  // Materialize an isolated temporary landing workspace as a `git clone
  // --shared` of the workflow workspace. The landing workspace is a fully
  // independent repository that references the workflow workspace's
  // object store via alternates (read-only), so removing the landing
  // directory cannot delete or corrupt the workflow workspace's git
  // objects, refs, or branch. The `origin` remote is reset to the
  // configured repository gitUrl so push operations in the landing
  // workspace target the real upstream.
  async createLandingWorkspace(work: WorkItem, signal: AbortSignal): Promise<LandingWorkspaceInfo> {
    const variables = work.variables ?? {}
    const gitUrl = stringAt(variables, ["repository", "gitUrl"])
    const baseBranch = stringAt(variables, ["repository", "baseBranch"])
    if (!gitUrl) throw new Error("Landing workspace requires repository.gitUrl in variables.")
    const effectiveBaseBranch = baseBranch ?? "main"
    const runId = stringAt(variables, ["mohist", "runId"]) ?? work.workflowRunId ?? ""
    const runBranch = runBranchName(runId)

    const projectId = stringAt(variables, ["project", "id"]) ?? "project"
    const projectName = stringAt(variables, ["project", "name"]) ?? projectId

    const suppliedPath = stringAt(variables, ["workspace", "path"])
    const workspacePath = suppliedPath
      ? resolve(suppliedPath)
      : issueWorkspacePath(this.runnerRoot, projectName, numberAt(variables, ["issue", "number"]) ?? 0)
    if (!exists(workspacePath)) {
      throw new Error(`Workflow workspace ${workspacePath} must exist before creating a landing workspace.`)
    }

    const landingPath = landingWorkspacePath(this.runnerRoot, projectName, runId)
    await ensureDir(join(landingPath, ".."))
    // `git clone --shared` defaults to --single-branch, which only
    // materializes the branch that is checked out in the source. We need
    // both the base branch and the per-run branch refs to be visible in
    // the landing clone, so use --no-single-branch and then explicitly
    // fetch all local refs from the workspace. This keeps the landing
    // clone's object store shared with the workflow workspace via
    // alternates (read-only), so removing the landing directory cannot
    // affect the workflow workspace's refs or objects.
    const result = await runCommand("git", ["clone", "--shared", "--no-single-branch", workspacePath, landingPath], ".", signal)
    if (result.exitCode !== 0) {
      // best-effort cleanup on failed clone
      await deleteDirectory(landingPath)
      throw new Error(`git clone --shared for landing workspace failed: ${result.stderr || result.stdout}`)
    }

    // The clone checks out the workspace's current branch (the run
    // branch). `git fetch` refuses to update the currently checked-out
    // branch, so detach HEAD first; we will leave the run branch
    // ref alone (the clone already created it pointing at the right
    // commit) and fetch the remaining local refs (base branch + any
    // others) explicitly.
    const detach = await runCommand("git", ["-C", landingPath, "checkout", "--detach", "HEAD"], landingPath, signal)
    if (detach.exitCode !== 0) {
      await deleteDirectory(landingPath)
      throw new Error(`git checkout --detach HEAD in landing workspace failed: ${detach.stderr || detach.stdout}`)
    }

    // Pull all the workflow workspace's local refs (base branch + any
    // other refs the workflow has produced, excluding the run branch
    // which is already correct) into the landing clone so
    // publish/preflight can read and base-branch-checkout against them
    // without going back to the workflow workspace.
    const fetchResult = await runCommand(
      "git",
      ["-C", landingPath, "fetch", "origin", "+refs/heads/*:refs/heads/*"],
      landingPath,
      signal,
    )
    if (fetchResult.exitCode !== 0) {
      await deleteDirectory(landingPath)
      throw new Error(`git fetch refs/heads/* in landing workspace failed: ${fetchResult.stderr || fetchResult.stdout}`)
    }

    // Reset origin to the configured remote gitUrl so push operations in
    // the landing workspace target the real upstream, not the workflow
    // workspace's local cache clone.
    const setUrl = await runCommand("git", ["-C", landingPath, "remote", "set-url", "origin", gitUrl], landingPath, signal)
    if (setUrl.exitCode !== 0) {
      await deleteDirectory(landingPath)
      throw new Error(`git remote set-url failed in landing workspace: ${setUrl.stderr || setUrl.stdout}`)
    }

    return { path: landingPath, runId, runBranch, baseBranch: effectiveBaseBranch, gitUrl }
  }

  // Best-effort disposal of an isolated landing workspace. The landing
  // workspace is a `--shared` clone of the workflow workspace, so a
  // recursive `rm` of the landing directory only removes the clone's own
  // working tree, index, and ref files; the workflow workspace's object
  // store (shared via alternates) is read-only and untouched. A failure
  // here is reported via the returned `disposed: false` so callers can
  // surface it without losing the landing path that needs follow-up.
  async disposeLandingWorkspace(landing: LandingWorkspaceInfo | string, signal: AbortSignal): Promise<{ path: string, disposed: boolean, error?: string }> {
    const path = typeof landing === "string" ? landing : landing.path
    if (!exists(path)) return { path, disposed: true }
    try {
      await deleteDirectory(path)
      return { path, disposed: true }
    } catch (err) {
      return { path, disposed: false, error: err instanceof Error ? err.message : String(err) }
    }
  }

  // Remove landing workspaces left behind by previous runs. Pruning is
  // scoped to the project's landing directory and only removes entries
  // that match the prior runId (or entries with the issue runId
  // pattern), so a crash in one run cannot leak landing dirs that
  // affect a concurrent or future run.
  async pruneLandingWorkspaces(variables: JsonObject | undefined, runId: string | null | undefined, signal: AbortSignal): Promise<string[]> {
    const projectId = stringAt(variables, ["project", "id"]) ?? "project"
    const projectName = stringAt(variables, ["project", "name"]) ?? projectId
    const landingRoot = landingRootPath(this.runnerRoot, projectName)
    if (!exists(landingRoot)) return []

    const safeRunId = landingSafeId(runId)
    const removed: string[] = []
    const entries = await readdir(landingRoot, { withFileTypes: true })
    for (const entry of entries) {
      if (!entry.isDirectory()) continue
      // Landing dir naming: `<runId>-<uuid>`. Always remove dirs whose
      // runId prefix matches the run we are now ensuring (a crashed
      // run will not race with a single concurrent ensure of the same
      // runId). The uuid disambiguates concurrent retries of the same
      // runId and is preserved on creation.
      if (!entry.name.startsWith(`${safeRunId}-`)) continue
      const target = join(landingRoot, entry.name)
      try {
        await deleteDirectory(target)
        removed.push(target)
      } catch {
        // best-effort; the next ensure will retry.
      }
    }
    return removed
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

function landingRootPath(runnerRoot: string, projectName: string) {
  return resolve(join(runnerRoot, slug(projectName), "landing"))
}

function landingWorkspacePath(runnerRoot: string, projectName: string, runId: string) {
  return resolve(join(landingRootPath(runnerRoot, projectName), `${landingSafeId(runId)}-${randomUUID()}`))
}

function landingSafeId(runId: string | null | undefined) {
  const safe = (runId ?? "").replace(/[^A-Za-z0-9_-]/g, "")
  return safe || "run"
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
