import { constants } from "node:fs"
import { lstat, mkdir, open, readdir, rename } from "node:fs/promises"
import { homedir, tmpdir } from "node:os"
import { isAbsolute, join, relative, resolve } from "node:path"
import type { JsonObject, RenderedWorkItem } from "../core/types.js"
import { getSegments, stringAt } from "../core/json-path.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../actions/git.js"
import { deleteDirectory, ensureDir, exists, readText, runCommand, writeText, type CommandResult } from "../system/process.js"
import type { WorkspaceRegistry } from "./workspace-registry.js"
import type { TaskLogger } from "./task-log.js"

/**
 * `source` tag recorded against every captured workspace-preparation
 * line. Distinct from the action body's `action:*` tag so the web
 * viewer can phase-distinguish the clone / branch / worktree setup
 * from the action itself.
 */
export const WORKSPACE_PREP_SOURCE = "workspace-prep"

function workspacePrepSink(log: TaskLogger | null | undefined) {
  return log ? { log, source: WORKSPACE_PREP_SOURCE } : undefined
}

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

export interface WorkspaceNetworkTimeoutStep {
  name: string
  command: string
  exitCode: number
  output: string
  status: "timeout"
  timeoutMs?: number
}

export class WorkspaceNetworkTimeoutError extends Error {
  readonly kind = "workspace-network-timeout"
  constructor(message: string, readonly step: WorkspaceNetworkTimeoutStep) {
    super(message)
    this.name = "WorkspaceNetworkTimeoutError"
  }
}

// The workflow workspace is just a clone of the project repo checked out
// on a per-run branch. Preparing it is two steps: (1) have a clone at the
// workspace path, (2) be on the run branch. The run branch is the
// identity — its presence at a path means "this run is already set up
// here", so re-entering a run is cheap (just switch to its branch) and a
// new run at a reused path is a pristine re-clone. No marker file, no
// shared bare cache, no alternates.

export interface WorkspaceInfo {
  path: string
  branch?: string | null
  changeDir?: string | null
}

export class WorkspaceManager {
  constructor(
    private readonly runnerRoot = defaultRunnerRoot(),
    private readonly registry: WorkspaceRegistry | null = null,
  ) {}

  // Ensure this run has a usable workspace: a clone of the repo on the
  // run branch. Idempotent — a workspace already on this run's branch is
  // left alone (cheap re-entry); anything else is (re)created from the
  // latest base.
  async prepare(work: RenderedWorkItem, signal: AbortSignal, log: TaskLogger | null = null): Promise<WorkspaceInfo> {
    const variables = work.variables ?? {}
    const gitUrl = stringAt(variables, ["repository", "gitUrl"])
    const baseBranch = stringAt(variables, ["repository", "baseBranch"])
    const issueNumber = numberAt(variables, ["issue", "number"])
    if (!gitUrl || !baseBranch || issueNumber === undefined) {
      throw new Error(`Workspace requires repository.gitUrl, repository.baseBranch, and issue.number. Got gitUrl=${gitUrl ?? "null"}, baseBranch=${baseBranch ?? "null"}, issueNumber=${issueNumber ?? "undefined"}`)
    }

    const runId = work.workflowRunId
    const expected = workspaceIdentity(work, variables, runId)
    const runBranch = expected.runBranch
    const changeDir = stringAt(variables, ["openspecChangeDir"])
    const workspacePath = issueWorkspacePath(this.runnerRoot, runId)
    const workspaceExistedBeforePreparation = pathExists(workspacePath)
    if (!workspaceExistedBeforePreparation) {
      await this.verifyBaseBranch(gitUrl, baseBranch, signal, log)
    }
    await withManagedWorkspacePath(this.runnerRoot, workspacePath, false, async (managedWorkspacePath) => {
      if (await pathExists(managedWorkspacePath)) {
        await validateWorkspaceIdentity(managedWorkspacePath, expected, gitUrl, signal, log)
        if (!await this.hasRunBranch(managedWorkspacePath, runBranch, signal, log)) {
          throw new WorkspaceIdentityMismatchError(`Workflow workspace ${workspacePath} has no branch ${runBranch}; refusing to mutate an existing workspace.`, workspacePath, expected)
        }
        await this.reenterRunBranch(managedWorkspacePath, runBranch, signal, log)
      } else {
        await this.bootstrap(managedWorkspacePath, gitUrl, baseBranch, expected, signal, log, workspaceExistedBeforePreparation)
      }
    })

    if (this.registry) {
      await this.registry.register({
        issueNumber,
        workflowRunId: expected.workflowRunId,
        workspacePath,
        runBranch: expected.runBranch,
      })
    }
    return { path: workspacePath, branch: runBranch, changeDir: changeDir ? join(workspacePath, changeDir) : null }
  }

  async verify(work: RenderedWorkItem, signal: AbortSignal, log: TaskLogger | null = null): Promise<WorkspaceInfo> {
    const variables = work.variables ?? {}
    const issueNumber = numberAt(variables, ["issue", "number"])
    const runId = work.workflowRunId
    const gitUrl = stringAt(variables, ["repository", "gitUrl"])
    const baseBranch = stringAt(variables, ["repository", "baseBranch"])
    if (!gitUrl || !baseBranch || issueNumber === undefined) {
      throw new WorkspaceIdentityMismatchError("Issue workspace identity is incomplete")
    }
    const expected = workspaceIdentity(work, variables, runId)
    const runBranch = expected.runBranch
    const changeDir = stringAt(variables, ["openspecChangeDir"])

    const workspacePath = issueWorkspacePath(this.runnerRoot, runId)
    await withManagedWorkspacePath(this.runnerRoot, workspacePath, true, async (managedWorkspacePath) => {
      await validateWorkspaceIdentity(managedWorkspacePath, expected, gitUrl, signal, log)

      // Health gate: every dispatch passes through verify(), so this is
      // the per-task entry point. A residual rebase / merge / cherry-pick
      // from a prior mid-flight crash is detected and aborted here, BEFORE
      // the marker / branch checks below — otherwise a `git checkout` from
      // the residual state would refuse with "resolve your current index
      // first" (the #166 fatality). The gate is non-destructive: the
      // `reset --hard <runBranch>` aligns the tree to the run branch ref,
      // which has not moved because the failed rebase never advanced it.
      await this.runHealthGate(managedWorkspacePath, runBranch, signal, log)

      if (!exists(managedWorkspacePath)) {
        throw new WorkspaceMissingError(
          `Workflow workspace ${workspacePath} is missing; workflow start materialization did not produce a bound workspace for this run.`,
          workspacePath,
        )
      }

      await verifyWorkspaceBranch(managedWorkspacePath, runBranch, signal, log)
    })

    if (this.registry) {
      await this.registry.refreshMaterializedAt(runId)
    }

    return {
      path: workspacePath,
      branch: runBranch,
      changeDir: changeDir ? join(workspacePath, changeDir) : null,
    }
  }

  private async bootstrap(workspacePath: string, gitUrl: string, baseBranch: string, expected: IssueWorkspaceMarker, signal: AbortSignal, log: TaskLogger | null, verifyBaseBranch: boolean): Promise<void> {
    const preparationPath = `${workspacePath}.preparing`
    if (await pathExists(preparationPath)) await deleteDirectory(preparationPath)
    await assertNotSymlink(preparationPath)
    if (verifyBaseBranch) await this.verifyBaseBranch(gitUrl, baseBranch, signal, log)
    await this.cloneFresh(preparationPath, gitUrl, signal, log)
    await validateWorkspaceOrigin(preparationPath, gitUrl, signal, log)
    await this.restoreOrCreateRunBranch(preparationPath, baseBranch, expected.runBranch, signal, log)
    await ensureMarkerExcluded(preparationPath)
    await writeText(markerPath(preparationPath), JSON.stringify(expected, null, 2))
    await validateWorkspaceIdentity(preparationPath, expected, gitUrl, signal, log)
    await rename(preparationPath, workspacePath)
  }

  // True only when <path> is a git clone that already has <runBranch> —
  // i.e. this run is already set up here. Everything else (missing dir,
  // non-git dir, a previous run's clone) is treated as "not prepared".
  private async hasRunBranch(workspacePath: string, runBranch: string, signal: AbortSignal, log: TaskLogger | null = null): Promise<boolean> {
    if (!exists(workspacePath) || !exists(join(workspacePath, ".git"))) return false
    const sink = workspacePrepSink(log)
    const result = await runCommand("git", ["-C", workspacePath, "rev-parse", "--verify", `refs/heads/${runBranch}`], ".", signal, undefined, sink ? { onLine: (line) => sink.log.write(sink.source, line) } : undefined)
    return result.exitCode === 0
  }

  private async cloneFresh(workspacePath: string, gitUrl: string, signal: AbortSignal, log: TaskLogger | null = null): Promise<void> {
    if (exists(workspacePath)) await deleteDirectory(workspacePath)
    await ensureDir(join(workspacePath, ".."))
    const sink = workspacePrepSink(log)
    const result = await runCommand("git", ["clone", gitUrl, workspacePath], ".", signal, undefined, sink ? { onLine: (line) => sink.log.write(sink.source, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS } : { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS })
    if (result.exitCode !== 0) {
      // Drop any partial clone git left behind so a retry starts clean.
      await deleteDirectory(workspacePath).catch(() => {})
      if (result.status === "timeout") throw workspaceNetworkTimeout("git-clone", `clone ${gitUrl} ${workspacePath}`, result)
      throw new Error(`git clone failed for ${gitUrl}: ${result.stderr || result.stdout}`)
    }
  }

  // Create the run branch off the latest base. A fresh clone already has
  // up-to-date origin/<base> refs, so no separate fetch is needed.
  private async restoreOrCreateRunBranch(workspacePath: string, baseBranch: string, runBranch: string, signal: AbortSignal, log: TaskLogger | null = null): Promise<void> {
    const sink = workspacePrepSink(log)
    const branchRef = `refs/remotes/origin/${runBranch}`
    const existing = await runCommand("git", ["-C", workspacePath, "show-ref", "--verify", "--quiet", branchRef], workspacePath, signal, undefined, sink ? { onLine: (line) => sink.log.write(sink.source, line) } : undefined)
    const source = existing.exitCode === 0 ? `origin/${runBranch}` : `origin/${baseBranch}`
    const create = await runCommand("git", ["-C", workspacePath, "checkout", "-B", runBranch, source], workspacePath, signal, undefined, sink ? { onLine: (line) => sink.log.write(sink.source, line) } : undefined)
    if (create.exitCode !== 0) {
      throw new Error(`Configured base branch '${baseBranch}' cannot be resolved from repository gitUrl.`)
    }
  }

  // Fail fast — before creating anything on disk — when the configured
  // base branch genuinely does not exist at the source. A non-zero exit
  // (repo unreachable / auth) is left for the clone step to surface with
  // its own error; only a reachable repo with an absent branch fails here.
  private async verifyBaseBranch(gitUrl: string, baseBranch: string, signal: AbortSignal, log: TaskLogger | null = null): Promise<void> {
    const sink = workspacePrepSink(log)
    const result = await runCommand("git", ["ls-remote", "--heads", gitUrl, baseBranch], ".", signal, undefined, sink ? { onLine: (line) => sink.log.write(sink.source, line), timeoutMs: NETWORK_COMMAND_TIMEOUT_MS } : { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS })
    if (result.status === "timeout") throw workspaceNetworkTimeout("git-ls-remote", `ls-remote --heads ${gitUrl} ${baseBranch}`, result)
    if (result.exitCode === 0 && result.stdout.trim() === "") {
      throw new Error(`Configured base branch '${baseBranch}' cannot be resolved from repository gitUrl.`)
    }
  }

  // Switch an already-prepared workspace back onto its run branch. A
  // rebase/merge/cherry-pick that crashed mid-flight leaves the work tree
  // unusable; the run branch ref itself is untouched (git only advances it
  // on success), so aborting the op and resetting to the ref realigns the
  // tree without losing the run's commits.
  private async reenterRunBranch(workspacePath: string, runBranch: string, signal: AbortSignal, log: TaskLogger | null = null): Promise<void> {
    const sink = workspacePrepSink(log)
    const lineOptions = sink ? { onLine: (line: string) => sink.log.write(sink.source, line) } : undefined
    const checkout = await runCommand("git", ["-C", workspacePath, "checkout", runBranch], workspacePath, signal, undefined, lineOptions)
    if (checkout.exitCode === 0) return
    await runCommand("git", ["-C", workspacePath, "rebase", "--abort"], workspacePath, signal, undefined, lineOptions).catch(() => {})
    await runCommand("git", ["-C", workspacePath, "merge", "--abort"], workspacePath, signal, undefined, lineOptions).catch(() => {})
    await runCommand("git", ["-C", workspacePath, "cherry-pick", "--abort"], workspacePath, signal, undefined, lineOptions).catch(() => {})
    await runCommand("git", ["-C", workspacePath, "checkout", runBranch], workspacePath, signal, undefined, lineOptions).catch(() => {})
    const reset = await runCommand("git", ["-C", workspacePath, "reset", "--hard", runBranch], workspacePath, signal, undefined, lineOptions)
    if (reset.exitCode !== 0) {
      throw new Error(`Could not restore workspace to run branch ${runBranch}: ${checkout.stderr || reset.stderr || reset.stdout}`)
    }
  }

  private async runHealthGate(workspacePath: string, runBranch: string, signal: AbortSignal, log: TaskLogger | null = null): Promise<void> {
    if (!exists(workspacePath)) return
    if (!exists(join(workspacePath, ".git"))) return

    const residual = await this.detectResidualState(workspacePath, signal)
    if (!residual) return

    const sink = workspacePrepSink(log)
    const lineOptions = sink ? { onLine: (line: string) => sink.log.write(sink.source, line) } : undefined

    if (residual === "rebase") {
      await runCommand("git", ["-C", workspacePath, "rebase", "--abort"], workspacePath, signal, undefined, lineOptions)
    } else if (residual === "merge") {
      await runCommand("git", ["-C", workspacePath, "merge", "--abort"], workspacePath, signal, undefined, lineOptions)
    } else if (residual === "cherry-pick") {
      await runCommand("git", ["-C", workspacePath, "cherry-pick", "--abort"], workspacePath, signal, undefined, lineOptions)
    }

    const reset = await runCommand("git", ["-C", workspacePath, "reset", "--hard", runBranch], workspacePath, signal, undefined, lineOptions)
    if (reset.exitCode !== 0) {
      throw new Error(`runHealthGate: could not reset workspace to ${runBranch}: ${reset.stderr || reset.stdout}`)
    }
  }

  private async detectResidualState(workspacePath: string, signal: AbortSignal): Promise<"rebase" | "merge" | "cherry-pick" | null> {
    if (exists(join(workspacePath, ".git", "rebase-merge")) || exists(join(workspacePath, ".git", "rebase-apply"))) {
      return "rebase"
    }
    if (exists(join(workspacePath, ".git", "MERGE_HEAD"))) {
      return "merge"
    }
    if (exists(join(workspacePath, ".git", "CHERRY_PICK_HEAD"))) {
      return "cherry-pick"
    }
    return null
  }
}

function workspaceNetworkTimeout(name: string, command: string, result: CommandResult): WorkspaceNetworkTimeoutError {
  const output = [result.stdout.trim(), result.stderr.trim()].filter(Boolean).join("\n")
  return new WorkspaceNetworkTimeoutError(
    `Workspace preparation network command timed out: ${name} (${command}) after ${(result.timeoutMs ?? NETWORK_COMMAND_TIMEOUT_MS) / 1000}s`,
    { name, command, exitCode: result.exitCode, output, status: "timeout", timeoutMs: result.timeoutMs },
  )
}

export function defaultRunnerRoot() {
  return process.env.MOHIST_RUNNER_ROOT ?? process.env.MOHIST_WORKSPACE_ROOT ?? join(homedir(), ".mohist", "projects")
}

export function runnerVariables() {
  return { os: process.platform, hostname: process.env.COMPUTERNAME ?? process.env.HOSTNAME ?? "unknown", temp: tmpdir() }
}

export function issueWorkspacePath(runnerRoot: string, workflowRunId: string) {
  if (!/^wr[-_A-Za-z0-9]+$/.test(workflowRunId)) throw new WorkspaceIdentityMismatchError("Invalid workflow run id")
  return resolve(join(runnerRoot, "workspaces", workflowRunId))
}

function runBranchName(runId: string | null | undefined) {
  const safe = (runId ?? "").replace(/[^A-Za-z0-9_-]/g, "")
  return safe ? `mohist/run-${safe}` : "mohist/run"
}

export interface IssueWorkspaceMarker {
  workflowRunId: string
  runBranch: string
}

function workspaceIdentity(work: RenderedWorkItem, variables: JsonObject, workflowRunId: string): IssueWorkspaceMarker {
  const variableRunId = stringAt(variables, ["mohist", "runId"])
  if (variableRunId && variableRunId !== workflowRunId) throw new WorkspaceIdentityMismatchError("Dispatch workflowRunId does not match the authoritative run identity")
  return {
    workflowRunId,
    runBranch: runBranchName(workflowRunId),
  }
}

// Read the workspace marker from disk. Returns `null` when the marker
// is missing or unreadable; the caller decides what kind of failure
// that is (corrupt vs missing). Used by both `verify()` (which needs
// to distinguish missing / corrupt / mismatch) and `planResolution()`
// (which just needs a yes/no answer).
export async function readMarker(workspacePath: string): Promise<Partial<IssueWorkspaceMarker> | null> {
  const path = markerPath(workspacePath)
  if (!exists(path)) return null
  try {
    const raw = await readText(path)
    return JSON.parse(raw) as Partial<IssueWorkspaceMarker>
  } catch {
    return null
  }
}

export async function readMarkerWorkflowRunId(workspacePath: string): Promise<string | null | undefined> {
  const marker = await readMarker(workspacePath)
  return marker?.workflowRunId
}

export async function validateWorkspaceIdentity(workspacePath: string, expected: IssueWorkspaceMarker, gitUrl: string, signal: AbortSignal, log: TaskLogger | null = null, runnerRoot?: string): Promise<void> {
  if (runnerRoot) await assertManagedWorkspacePath(runnerRoot, workspacePath, true)
  const marker = await readMarker(workspacePath)
  if (!marker) {
    throw new WorkspaceCorruptError(`Workflow workspace ${workspacePath} has no readable identity marker`, workspacePath)
  }
  const fields: (keyof IssueWorkspaceMarker)[] = ["workflowRunId", "runBranch"]
  if (fields.some((field) => marker[field] !== expected[field])) {
    throw new WorkspaceIdentityMismatchError(`Workflow workspace ${workspacePath} marker identity does not match the requested run`, workspacePath, expected, marker)
  }
  await validateWorkspaceOrigin(workspacePath, gitUrl, signal, log)
}

async function validateWorkspaceOrigin(workspacePath: string, gitUrl: string, signal: AbortSignal, log: TaskLogger | null = null): Promise<void> {
  const sink = workspacePrepSink(log)
  const options = sink ? { onLine: (line: string) => sink.log.write(sink.source, line) } : undefined
  const result = await runCommand("git", ["-C", workspacePath, "remote", "get-url", "origin"], ".", signal, undefined, options)
  if (result.exitCode !== 0 || result.stdout.trim() !== gitUrl.trim()) {
    throw new WorkspaceIdentityMismatchError(`Workflow workspace ${workspacePath} origin does not match the requested repository`, workspacePath)
  }
}

async function assertManagedWorkspacePath(runnerRoot: string, candidate: string, requireFinal: boolean): Promise<void> {
  const root = resolve(runnerRoot)
  const target = resolve(candidate)
  const rel = relative(root, target)
  if (!rel || rel.startsWith("..") || isAbsolute(rel)) {
    throw new WorkspaceIdentityMismatchError(`Workspace path ${target} is outside runner root ${root}`, target)
  }
  try {
    if ((await lstat(root)).isSymbolicLink()) throw new WorkspaceIdentityMismatchError(`Runner root ${root} is symlinked`, target)
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error
  }
  const components = rel.split(/[\\/]+/).filter(Boolean)
  let current = root
  for (let i = 0; i < components.length; i++) {
    current = join(current, components[i]!)
    try {
      const stat = await lstat(current)
      if (stat.isSymbolicLink()) throw new WorkspaceIdentityMismatchError(`Workspace path ${current} is symlinked`, target)
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") {
        if (i === components.length - 1 && !requireFinal) return
        continue
      }
      throw error
    }
  }
  if (requireFinal && !pathExists(target)) {
    throw new WorkspaceMissingError(`Workflow workspace ${target} is missing`, target)
  }
}

export async function withManagedWorkspacePath<T>(
  runnerRoot: string,
  workspacePath: string,
  requireFinal: boolean,
  operation: (managedWorkspacePath: string) => Promise<T>,
): Promise<T> {
  const root = resolve(runnerRoot)
  const workspaceParent = join(root, "workspaces")
  const target = resolve(workspacePath)
  const name = relative(workspaceParent, target)
  if (!name || name.includes("/") || name.includes("\\") || isAbsolute(name)) {
    throw new WorkspaceIdentityMismatchError(`Workspace path ${target} is outside managed workspace parent ${workspaceParent}`, target)
  }

  if (process.platform !== "linux") {
    await assertManagedWorkspacePath(root, target, requireFinal)
    return await operation(target)
  }

  await mkdir(root, { recursive: true })
  let rootHandle: Awaited<ReturnType<typeof open>> | undefined
  let workspaceHandle: Awaited<ReturnType<typeof open>> | undefined
  let managedWorkspacePath: string
  try {
    rootHandle = await open(root, constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW)
    const processFdRoot = `/proc/${process.pid}/fd`
    const stableRoot = join(processFdRoot, String(rootHandle.fd))
    await mkdir(join(stableRoot, "workspaces"), { recursive: true })
    workspaceHandle = await open(join(stableRoot, "workspaces"), constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW)
    managedWorkspacePath = join(processFdRoot, String(workspaceHandle.fd), name)
    await assertManagedWorkspaceEntry(managedWorkspacePath, target, requireFinal)
  } catch (error) {
    await workspaceHandle?.close()
    await rootHandle?.close()
    if (error instanceof WorkspaceMissingError || error instanceof WorkspaceIdentityMismatchError) throw error
    throw new WorkspaceIdentityMismatchError(`Managed workspace parent ${workspaceParent} is unavailable or symlinked`, target, undefined, undefined, error)
  }

  try {
    return await operation(managedWorkspacePath!)
  } finally {
    await workspaceHandle?.close()
    await rootHandle?.close()
  }
}

async function assertManagedWorkspaceEntry(managedWorkspacePath: string, workspacePath: string, requireFinal: boolean): Promise<void> {
  try {
    if ((await lstat(managedWorkspacePath)).isSymbolicLink()) {
      throw new WorkspaceIdentityMismatchError(`Workspace path ${workspacePath} is symlinked`, workspacePath)
    }
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error
    if (requireFinal) throw new WorkspaceMissingError(`Workflow workspace ${workspacePath} is missing`, workspacePath)
  }
}

async function assertNotSymlink(path: string): Promise<void> {
  try {
    if ((await lstat(path)).isSymbolicLink()) {
      throw new WorkspaceIdentityMismatchError(`Preparation path ${path} is symlinked`, path)
    }
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error
  }
}

function pathExists(path: string): boolean {
  return exists(path)
}

async function verifyWorkspaceBranch(workspacePath: string, expectedBranch: string, signal: AbortSignal, log: TaskLogger | null = null) {
  const sink = workspacePrepSink(log)
  const lineOptions = sink ? { onLine: (line: string) => sink.log.write(sink.source, line) } : undefined
  const branch = await runCommand("git", ["-C", workspacePath, "rev-parse", "--abbrev-ref", "HEAD"], ".", signal, undefined, lineOptions)
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
    const ref = await runCommand("git", ["-C", workspacePath, "rev-parse", "HEAD"], ".", signal, undefined, lineOptions)
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
// active workflow workspace clone under `<projectRoot>/workspaces/`.
// The scan follows transitive alternates so deleting the cache cannot
// corrupt active workspace object stores.
async function isCacheReferencedByActiveWorkspace(cachePath: string, projectRoot: string, signal: AbortSignal) {
  const target = resolve(join(cachePath, "objects"))
  const cloneRoots = [join(projectRoot, "workspaces")]

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

function numberAt(value: JsonObject | undefined, path: string[]): number | undefined {
  const found = getSegments(value, path)
  return typeof found === "number" ? found : undefined
}
