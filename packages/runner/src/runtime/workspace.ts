import { randomUUID } from "node:crypto"
import { readdir } from "node:fs/promises"
import { homedir, tmpdir } from "node:os"
import { join, resolve } from "node:path"
import type { JsonObject, WorkItem } from "../core/types.js"
import { deleteDirectory, ensureDir, exists, readText, runCommand, writeText } from "../system/process.js"

// Marker for a refused cache replacement. Replacement was justified
// (origin identity mismatch or verified corruption) but an active
// workspace references the cache's object store through alternates, so
// the cache must NOT be deleted. The object store is preserved until
// active references are released.
export class CacheReplacementBlockedError extends Error {
  readonly kind = "cache-replacement-blocked"
  constructor(message: string, readonly cause?: unknown) {
    super(message)
    this.name = "CacheReplacementBlockedError"
  }
}

// Workspace dispatch-time infrastructure failures. Each carries a
// distinct `kind` so CLI/API/UI can render a "workflow-start
// workspace-materialization failure" distinct from ordinary task
// failures (dirty-worktree, conflict, base-moved). The kinds are the
// runner-side of the workspace-materialization failure-kind taxonomy;
// T-003 maps them through the CLI/web surface.
//
// - workspace-missing: the workflow workspace path does not exist at
//   dispatch time. The bound workspace identity says it SHOULD be on
//   disk; the disk says otherwise. Recoverable only by re-materializing
//   (which the dispatch-time contract explicitly refuses), so this is
//   attributed to workflow infrastructure.
// - workspace-corrupt: the workspace exists but the marker is missing
//   or unreadable. The runner cannot trust that this directory is the
//   bound workflow workspace, so it refuses to dispatch into it.
// - workspace-identity-mismatch: the workspace exists AND has a marker,
//   but the marker is bound to a different workflow run (issueId /
//   issueNumber / workflowRunId does not match this dispatch).
//   Re-cloning would discard in-progress work on the run branch, so
//   the runner refuses to recover this by materializing again.
//
// Branch mismatches reuse the existing branch-invariant-violation kind
// (runner/action bug at the task boundary) per the spec's "Agent-job
// standalone workspaces are exempt" rule and the existing
// branch-stability contract.
export class WorkspaceMissingError extends Error {
  readonly kind = "workspace-missing"
  constructor(message: string, readonly workspacePath?: string, readonly cause?: unknown) {
    super(message)
    this.name = "WorkspaceMissingError"
  }
}

export class WorkspaceCorruptError extends Error {
  readonly kind = "workspace-corrupt"
  constructor(message: string, readonly workspacePath?: string, readonly cause?: unknown) {
    super(message)
    this.name = "WorkspaceCorruptError"
  }
}

export class WorkspaceIdentityMismatchError extends Error {
  readonly kind = "workspace-identity-mismatch"
  constructor(message: string, readonly workspacePath?: string, readonly expected?: IssueWorkspaceMarker, readonly actual?: Partial<IssueWorkspaceMarker>, readonly cause?: unknown) {
    super(message)
    this.name = "WorkspaceIdentityMismatchError"
  }
}

export class WorkspaceBranchMismatchError extends Error {
  readonly kind = "branch-invariant-violation"
  constructor(message: string, readonly workspacePath: string, readonly expectedBranch: string, readonly observedBranch: string | null, readonly observedRef: string | null = null, readonly detail?: string) {
    super(message)
    this.name = "WorkspaceBranchMismatchError"
  }
}

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
    const plan = await this.planResolution(work, signal)
    if (plan.action === "materialize") {
      return await this.materialize(work, signal)
    }
    return await this.verify(work, signal)
  }

  async materialize(work: WorkItem, signal: AbortSignal): Promise<WorkspaceInfo> {
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
    const projectRoot = resolve(join(this.runnerRoot, slug(projectName)))
    await this.ensureCache(cachePath, gitUrl, projectRoot, effectiveBaseBranch, signal)
    await this.resolveBranch(cachePath, effectiveBaseBranch, signal)

    const suppliedPath = stringAt(variables, ["workspace", "path"])
    const workspacePath = suppliedPath
      ? resolve(suppliedPath)
      : issueWorkspacePath(this.runnerRoot, projectName, issueNumber)
    const marker = issueWorkspaceMarker(variables)
    const workspaceExistsBeforeCacheRepair = exists(workspacePath)
    if (workspaceExistsBeforeCacheRepair && !await hasSameMarker(workspacePath, marker)) {
      await deleteDirectory(workspacePath)
    }
    await this.ensureFreshWorkspace(cachePath, workspacePath, effectiveBaseBranch, runBranch, gitUrl, marker, signal)

    // Prune any landing workspaces left behind by a previous, possibly
    // crashed, run. Each landing is runId-scoped (and uuid-disambiguated),
    // so removing them cannot affect a different run and cannot reach
    // into the workflow workspace's object store — landing clones are
    // isolated `--shared` alternates clones of the workflow workspace.
    await this.pruneLandingWorkspaces(variables, runId, signal)

    const changeDir = stringAt(variables, ["openspecChangeDir"])
    if (changeDir) await ensureDir(join(workspacePath, changeDir, "specs"))
    await ensureMarkerExcluded(workspacePath)
    await writeText(markerPath(workspacePath), JSON.stringify(marker, null, 2))
    return { path: workspacePath, branch: runBranch, changeDir: changeDir ? join(workspacePath, changeDir) : null }
  }

  async verify(work: WorkItem, signal: AbortSignal): Promise<WorkspaceInfo> {
    const variables = work.variables ?? {}
    const issueNumber = numberAt(variables, ["issue", "number"])
    const runId = stringAt(variables, ["mohist", "runId"]) ?? work.workflowRunId
    const runBranch = runBranchName(runId)
    const marker = issueWorkspaceMarker(variables)
    const changeDir = stringAt(variables, ["openspecChangeDir"])

    const projectName = stringAt(variables, ["project", "name"]) ?? stringAt(variables, ["project", "id"]) ?? "project"
    const suppliedPath = stringAt(variables, ["workspace", "path"])
    const workspacePath = suppliedPath
      ? resolve(suppliedPath)
      : issueWorkspacePath(this.runnerRoot, projectName, issueNumber ?? 0)

    if (!exists(workspacePath)) {
      throw new WorkspaceMissingError(
        `Workflow workspace ${workspacePath} is missing; workflow start materialization did not produce a bound workspace for this run.`,
        workspacePath,
      )
    }

    const markerOnDisk = await readMarker(workspacePath)
    if (markerOnDisk === null) {
      throw new WorkspaceCorruptError(
        `Workflow workspace ${workspacePath} has no marker at .mohist/workspace.json; the bound workspace identity cannot be verified.`,
        workspacePath,
      )
    }
    if (!markerMatches(markerOnDisk, marker)) {
      throw new WorkspaceIdentityMismatchError(
        `Workflow workspace ${workspacePath} marker is bound to a different run (expected ${formatIdentity(marker)}, found ${formatIdentity(markerOnDisk)}).`,
        workspacePath,
        marker,
        markerOnDisk,
      )
    }

    await verifyWorkspaceBranch(workspacePath, runBranch, signal)

    return {
      path: workspacePath,
      branch: runBranch,
      changeDir: changeDir ? join(workspacePath, changeDir) : null,
    }
  }

  async planResolution(work: WorkItem, signal: AbortSignal): Promise<{ action: "materialize" | "verify", workspacePath: string, marker?: Partial<IssueWorkspaceMarker> }> {
    const variables = work.variables ?? {}
    const issueNumber = numberAt(variables, ["issue", "number"])
    const projectName = stringAt(variables, ["project", "name"]) ?? stringAt(variables, ["project", "id"]) ?? "project"
    const suppliedPath = stringAt(variables, ["workspace", "path"])
    const workspacePath = suppliedPath
      ? resolve(suppliedPath)
      : issueWorkspacePath(this.runnerRoot, projectName, issueNumber ?? 0)
    if (!exists(workspacePath)) {
      return { action: "materialize", workspacePath }
    }
    const onDiskMarker = await readMarker(workspacePath)
    return { action: "verify", workspacePath, marker: onDiskMarker ?? undefined }
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

  private async ensureCache(cachePath: string, gitUrl: string, projectRoot: string, baseBranch: string, signal: AbortSignal) {
    if (!exists(cachePath)) {
      await this.cloneBareCache(cachePath, gitUrl, signal)
      return
    }

    const origin = await readCacheOrigin(cachePath, signal)
    const originMatches = origin === gitUrl
    const corrupt = await isCacheCorrupt(cachePath, baseBranch, signal)

    const replaceCache = async (reason: string) => {
      if (await isCacheReferencedByActiveWorkspace(cachePath, projectRoot, signal)) {
        throw new CacheReplacementBlockedError(
          `Cache ${cachePath} ${reason}; replacement is blocked because an active workspace still references its object store.`,
        )
      }
      await deleteDirectory(cachePath)
      await this.cloneBareCache(cachePath, gitUrl, signal)
    }

    if (originMatches) {
      if (corrupt) {
        await replaceCache("is corrupt")
        return
      }
      const fetch = await runCommand("git", ["-C", cachePath, "fetch", "origin"], ".", signal)
      if (fetch.exitCode !== 0) {
        if (await isCacheCorrupt(cachePath, baseBranch, signal)) await replaceCache("is corrupt")
        return
      }
      return
    }

    await replaceCache(`origin (${origin ?? "<unknown>"}) does not match ${gitUrl}`)
  }

  // Initial bare clone. Called when no prior cache exists; failure is
  // fatal (no fallback). For an existing cache, replacement paths use
  // the same clone primitive but gate the deletion on the reference
  // scan first.
  private async cloneBareCache(cachePath: string, gitUrl: string, signal: AbortSignal) {
    await ensureDir(join(cachePath, ".."))
    const result = await runCommand("git", ["clone", "--bare", gitUrl, cachePath], ".", signal)
    if (result.exitCode !== 0) {
      throw new Error(`git clone failed for ${gitUrl}: ${result.stderr || result.stdout}`)
    }
  }

  private async resolveBranch(cachePath: string, baseBranch: string, signal: AbortSignal) {
    const local = await runCommand("git", ["-C", cachePath, "rev-parse", "--verify", `refs/heads/${baseBranch}`], ".", signal)
    if (local.exitCode === 0) return
    const remote = await runCommand("git", ["-C", cachePath, "rev-parse", "--verify", `refs/remotes/origin/${baseBranch}`], ".", signal)
    if (remote.exitCode === 0) return
    throw new Error(`Configured base branch '${baseBranch}' cannot be resolved from repository gitUrl.`)
  }

  private async ensureFreshWorkspace(cachePath: string, workspacePath: string, baseBranch: string, runBranch: string, gitUrl: string, marker: IssueWorkspaceMarker, signal: AbortSignal) {
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

// Read the workspace marker from disk. Returns `null` when the marker
// is missing or unreadable; the caller decides what kind of failure
// that is (corrupt vs missing). Used by both `verify()` (which needs
// to distinguish missing / corrupt / mismatch) and `planResolution()`
// (which just needs a yes/no answer).
async function readMarker(workspacePath: string): Promise<Partial<IssueWorkspaceMarker> | null> {
  const path = markerPath(workspacePath)
  if (!exists(path)) return null
  try {
    const raw = await readText(path)
    return JSON.parse(raw) as Partial<IssueWorkspaceMarker>
  } catch {
    return null
  }
}

function markerMatches(actual: Partial<IssueWorkspaceMarker>, expected: IssueWorkspaceMarker): boolean {
  return actual.issueId === expected.issueId
    && actual.issueNumber === expected.issueNumber
    && actual.workflowRunId === expected.workflowRunId
}

function formatIdentity(marker: Partial<IssueWorkspaceMarker> | IssueWorkspaceMarker): string {
  return `issueId=${marker.issueId ?? "<null>"}, issueNumber=${marker.issueNumber ?? "<null>"}, workflowRunId=${marker.workflowRunId ?? "<null>"}`
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

async function verifyWorkspaceBranch(workspacePath: string, expectedBranch: string, signal: AbortSignal) {
  const branch = await runCommand("git", ["-C", workspacePath, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal)
  if (branch.exitCode !== 0) {
    throw new WorkspaceBranchMismatchError(
      `Workflow workspace ${workspacePath} branch probe failed; expected ${expectedBranch}.`,
      workspacePath,
      expectedBranch,
      null,
      null,
      branch.stderr || branch.stdout || `exit ${branch.exitCode}`,
    )
  }
  const observed = branch.stdout.trim()
  if (observed === expectedBranch) return
  if (observed === "HEAD") {
    const ref = await runCommand("git", ["-C", workspacePath, "rev-parse", "HEAD"], ".", signal)
    throw new WorkspaceBranchMismatchError(
      `Workflow workspace ${workspacePath} is detached; expected branch ${expectedBranch}.`,
      workspacePath,
      expectedBranch,
      null,
      ref.exitCode === 0 ? ref.stdout.trim() : null,
    )
  }
  throw new WorkspaceBranchMismatchError(
    `Workflow workspace ${workspacePath} is on branch ${observed}; expected ${expectedBranch}.`,
    workspacePath,
    expectedBranch,
    observed,
  )
}

function markerPath(workspacePath: string) {
  return join(workspacePath, ".mohist", "workspace.json")
}

async function ensureMarkerExcluded(workspacePath: string) {
  const excludePath = join(workspacePath, ".git", "info", "exclude")
  const markerRule = ".mohist/"
  let raw = ""
  try {
    raw = await readText(excludePath)
  } catch {
    // ignore
  }
  if (raw.split(/\r?\n/).some((line) => line.trim() === markerRule || line.trim() === ".mohist")) return
  const suffix = raw.endsWith("\n") || raw.length === 0 ? "" : "\n"
  await writeText(excludePath, `${raw}${suffix}${markerRule}\n`)
}

// Read the configured `origin` URL of a bare repository cache. Returns
// `undefined` if the cache is unreadable / unconfigured rather than
// throwing, so the caller can decide how to surface an unreadable cache
// (treat as identity mismatch → replacement candidate).
async function readCacheOrigin(cachePath: string, signal: AbortSignal) {
  const result = await runCommand("git", ["-C", cachePath, "remote", "get-url", "origin"], ".", signal)
  if (result.exitCode !== 0) return undefined
  return result.stdout.trim() || undefined
}

// Decide whether the cache's object store is still referenced by an
// active workspace clone. The check is scoped to the project root: only
// clones under `<projectRoot>/workspaces/` and `<projectRoot>/landing/`
// are scanned, which is where the runner actually creates `--shared`
// alternates clones.
//
// References can be transitive. A `--shared` clone's
// `.git/objects/info/alternates` lists sibling `<git_dir>/objects`
// paths; one of those alternates may itself be a `.git/objects` path
// belonging to another clone (the source clone of a `--shared` clone),
// which in turn has its own alternates chain. The scan follows the
// chain so a landing workspace whose alternates point at the workflow
// workspace (whose alternates point at the cache) is detected as a
// transitive reference. Deleting the cache in that situation would
// corrupt both clones' object stores.
//
// The scan tolerates missing directories, unreadable alternates
// files, and malformed entries — a corrupt alternates file in a stale
// landing dir is not a reason to refuse cache replacement. Only an
// unambiguous match (direct or transitive) triggers the block.
async function isCacheReferencedByActiveWorkspace(cachePath: string, projectRoot: string, signal: AbortSignal) {
  const target = resolve(join(cachePath, "objects"))
  const cloneRoots = [join(projectRoot, "workspaces"), join(projectRoot, "landing")]

  async function readAlternates(objectsDir: string): Promise<string[]> {
    const gitDir = objectsDir.replace(/[\\/]objects$/, "")
    const alternatesPath = join(gitDir, "objects", "info", "alternates")
    if (!exists(alternatesPath)) return []
    let raw: string
    try {
      raw = await readText(alternatesPath)
    } catch {
      return []
    }
    const out: string[] = []
    for (const line of raw.split(/\r?\n/)) {
      const trimmed = line.trim()
      if (!trimmed || trimmed.startsWith("#")) continue
      try {
        out.push(resolve(trimmed))
      } catch {
        // skip
      }
    }
    return out
  }

  for (const dir of cloneRoots) {
    if (!exists(dir)) continue
    const entries = await readdir(dir, { withFileTypes: true })
    for (const entry of entries) {
      if (!entry.isDirectory()) continue
      const gitDir = join(dir, entry.name, ".git")
      if (!exists(gitDir)) continue
      // BFS the alternates chain rooted at this clone. An alternates
      // entry is a `<git_dir>/objects` path; if it equals the target,
      // this clone references the cache. If it does not, but it is
      // itself a `.git/objects` path belonging to another clone, we
      // enqueue that clone's alternates to follow the chain further.
      const visited = new Set<string>()
      const queue: string[] = await readAlternates(join(gitDir, "objects"))
      while (queue.length > 0) {
        const current = queue.shift()!
        if (visited.has(current)) continue
        visited.add(current)
        if (current === target) return true
        // Only follow when the current entry looks like another clone's
        // `.git/objects` (i.e., ends with `.git/objects`). Other paths
        // (e.g., environment-provided object dirs) are leaf nodes.
        if (/(^|[\\/])\.git[\\/]objects$/.test(current)) {
          const next = await readAlternates(current)
          for (const n of next) if (!visited.has(n)) queue.push(n)
        }
      }
    }
  }
  return false
}

// `git fsck` based corruption detector. Runs an unconnected fsck
// against the bare cache; returns true when fsck reports any corrupt /
// missing object. Used as an alternate justification for cache
// replacement (per the spec's "origin URL mismatch OR verified
// corruption" rule).
async function isCacheCorrupt(cachePath: string, baseBranch: string, signal: AbortSignal) {
  const result = await runCommand("git", ["-C", cachePath, "fsck", "--full", "--no-progress"], ".", signal)
  if (result.exitCode !== 0) return true
  const base = await runCommand("git", ["-C", cachePath, "rev-parse", "--verify", `refs/heads/${baseBranch}^{commit}`], ".", signal)
  if (base.exitCode !== 0) return true
  const baseType = await runCommand("git", ["-C", cachePath, "cat-file", "-t", base.stdout.trim()], ".", signal)
  if (baseType.exitCode !== 0) return true
  const refs = await runCommand("git", ["-C", cachePath, "show-ref", "--heads", "--dereference"], ".", signal)
  if (refs.exitCode !== 0) return true
  for (const line of refs.stdout.split(/\r?\n/)) {
    const oid = line.trim().split(/\s+/)[0]
    if (!oid) continue
    const object = await runCommand("git", ["-C", cachePath, "cat-file", "-e", `${oid}^{object}`], ".", signal)
    if (object.exitCode !== 0) return true
    const tree = await runCommand("git", ["-C", cachePath, "ls-tree", "-r", oid], ".", signal)
    if (tree.exitCode !== 0) return true
  }
  return false
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
