import { homedir, tmpdir } from "node:os"
import { join, resolve } from "node:path"
import type { JsonObject, WorkItem } from "../core/types.js"
import { getSegments, stringAt } from "../core/json-path.js"
import { deleteDirectory, ensureDir, exists, readText, runCommand, writeText } from "../system/process.js"
import type { WorkspaceRegistry } from "./workspace-registry.js"

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
  async prepare(work: WorkItem, signal: AbortSignal): Promise<WorkspaceInfo> {
    const variables = work.variables ?? {}
    const gitUrl = stringAt(variables, ["repository", "gitUrl"])
    const baseBranch = stringAt(variables, ["repository", "baseBranch"]) ?? "main"
    const issueNumber = numberAt(variables, ["issue", "number"])

    if (!gitUrl || issueNumber === undefined) {
      throw new Error(`Workspace requires repository.gitUrl and issue.number in variables. Got gitUrl=${gitUrl ?? "null"}, issueNumber=${issueNumber ?? "undefined"}`)
    }

    const projectName = stringAt(variables, ["project", "name"]) ?? stringAt(variables, ["project", "id"]) ?? "project"
    const runId = stringAt(variables, ["mohist", "runId"]) ?? work.workflowRunId
    const runBranch = runBranchName(runId)
    const changeDir = stringAt(variables, ["openspecChangeDir"])

    const suppliedPath = stringAt(variables, ["workspace", "path"])
    const workspacePath = suppliedPath
      ? resolve(suppliedPath)
      : issueWorkspacePath(this.runnerRoot, projectName, issueNumber)

    if (await this.hasRunBranch(workspacePath, runBranch, signal)) {
      // Re-entry: this run already has its branch here. Switch back to it,
      // recovering from any in-flight rebase/merge that crashed mid-op.
      await this.reenterRunBranch(workspacePath, runBranch, signal)
    } else {
      // Fresh run at this path: a pristine clone + a new run branch off
      // the latest base. Any leftover directory (a previous run's) is
      // replaced so the new run starts clean.
      await this.verifyBaseBranch(gitUrl, baseBranch, signal)
      await this.cloneFresh(workspacePath, gitUrl, signal)
      await this.createRunBranch(workspacePath, baseBranch, runBranch, signal)
    }

    if (changeDir) await ensureDir(join(workspacePath, changeDir, "specs"))
    await ensureMarkerExcluded(workspacePath)
    await writeText(markerPath(workspacePath), JSON.stringify(marker, null, 2))
    if (this.registry) {
      await this.registry.register({
        issueId: marker.issueId,
        issueNumber: marker.issueNumber,
        workflowRunId: runId ?? "",
        workspacePath,
      })
    }
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

    // Health gate: every dispatch passes through verify(), so this is
    // the per-task entry point. A residual rebase / merge / cherry-pick
    // from a prior mid-flight crash is detected and aborted here, BEFORE
    // the marker / branch checks below — otherwise a `git checkout` from
    // the residual state would refuse with "resolve your current index
    // first" (the #166 fatality). The gate is non-destructive: the
    // `reset --hard <runBranch>` aligns the tree to the run branch ref,
    // which has not moved because the failed rebase never advanced it.
    await this.runHealthGate(workspacePath, runBranch, signal)

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

    if (this.registry) {
      await this.registry.refreshMaterializedAt(runId ?? "")
    }

    return {
      path: workspacePath,
      branch: runBranch,
      changeDir: changeDir ? join(workspacePath, changeDir) : null,
    }
  }

  // True only when <path> is a git clone that already has <runBranch> —
  // i.e. this run is already set up here. Everything else (missing dir,
  // non-git dir, a previous run's clone) is treated as "not prepared".
  private async hasRunBranch(workspacePath: string, runBranch: string, signal: AbortSignal): Promise<boolean> {
    if (!exists(workspacePath) || !exists(join(workspacePath, ".git"))) return false
    const result = await runCommand("git", ["-C", workspacePath, "rev-parse", "--verify", `refs/heads/${runBranch}`], ".", signal)
    return result.exitCode === 0
  }

  private async cloneFresh(workspacePath: string, gitUrl: string, signal: AbortSignal): Promise<void> {
    if (exists(workspacePath)) await deleteDirectory(workspacePath)
    await ensureDir(join(workspacePath, ".."))
    const result = await runCommand("git", ["clone", gitUrl, workspacePath], ".", signal)
    if (result.exitCode !== 0) {
      // Drop any partial clone git left behind so a retry starts clean.
      await deleteDirectory(workspacePath).catch(() => {})
      throw new Error(`git clone failed for ${gitUrl}: ${result.stderr || result.stdout}`)
    }
  }

  // Create the run branch off the latest base. A fresh clone already has
  // up-to-date origin/<base> refs, so no separate fetch is needed.
  private async createRunBranch(workspacePath: string, baseBranch: string, runBranch: string, signal: AbortSignal): Promise<void> {
    const create = await runCommand("git", ["-C", workspacePath, "checkout", "-b", runBranch, `origin/${baseBranch}`], workspacePath, signal)
    if (create.exitCode !== 0) {
      throw new Error(`Configured base branch '${baseBranch}' cannot be resolved from repository gitUrl.`)
    }
  }

  // Fail fast — before creating anything on disk — when the configured
  // base branch genuinely does not exist at the source. A non-zero exit
  // (repo unreachable / auth) is left for the clone step to surface with
  // its own error; only a reachable repo with an absent branch fails here.
  private async verifyBaseBranch(gitUrl: string, baseBranch: string, signal: AbortSignal): Promise<void> {
    const result = await runCommand("git", ["ls-remote", "--heads", gitUrl, baseBranch], ".", signal)
    if (result.exitCode === 0 && result.stdout.trim() === "") {
      throw new Error(`Configured base branch '${baseBranch}' cannot be resolved from repository gitUrl.`)
    }
  }

  // Switch an already-prepared workspace back onto its run branch. A
  // rebase/merge/cherry-pick that crashed mid-flight leaves the work tree
  // unusable; the run branch ref itself is untouched (git only advances it
  // on success), so aborting the op and resetting to the ref realigns the
  // tree without losing the run's commits.
  private async reenterRunBranch(workspacePath: string, runBranch: string, signal: AbortSignal): Promise<void> {
    const checkout = await runCommand("git", ["-C", workspacePath, "checkout", runBranch], workspacePath, signal)
    if (checkout.exitCode === 0) return
    await runCommand("git", ["-C", workspacePath, "rebase", "--abort"], workspacePath, signal).catch(() => {})
    await runCommand("git", ["-C", workspacePath, "merge", "--abort"], workspacePath, signal).catch(() => {})
    await runCommand("git", ["-C", workspacePath, "cherry-pick", "--abort"], workspacePath, signal).catch(() => {})
    await runCommand("git", ["-C", workspacePath, "checkout", runBranch], workspacePath, signal).catch(() => {})
    const reset = await runCommand("git", ["-C", workspacePath, "reset", "--hard", runBranch], workspacePath, signal)
    if (reset.exitCode !== 0) {
      throw new Error(`Could not restore workspace to run branch ${runBranch}: ${checkout.stderr || reset.stderr || reset.stdout}`)
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

function slug(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "") || "project"
}

export { slug as slugify }

function numberAt(value: JsonObject | undefined, path: string[]): number | undefined {
  const found = getSegments(value, path)
  return typeof found === "number" ? found : undefined
}
