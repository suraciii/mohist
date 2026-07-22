import type { JsonObject, DispatchWorkItem, WorkItemResult } from "../core/types.js"
import { git } from "./git-probe.js"
import type { TaskLogger } from "./task-log.js"

/**
 * `source` label recorded against every captured branch-stability
 * line. Distinct from the action body's `action:*` tag so the web
 * viewer can phase-distinguish the boundary probe from the action
 * itself.
 */
export const BRANCH_CHECK_SOURCE = "branch-check"

function branchCheckSink(log: TaskLogger | null | undefined) {
  return log ? { log, source: BRANCH_CHECK_SOURCE } : undefined
}

export interface BranchStabilityEvidence {
  kind: "branch-stability"
  boundary: "start" | "end"
  expectedBranch: string
  observedBranch: string
  observedRef?: string | null
}

export interface BranchInvariantViolationEvidence {
  kind: "branch-invariant-violation"
  boundary: "start" | "end"
  expectedBranch: string
  observedBranch: string
  observedRef?: string | null
  detail?: string
}

export interface CurrentBranchResult {
  branch: string | null
  ref: string | null
  detached: boolean
  nonGit: boolean
  error: string | null
}

export function expectedWorkspaceBranch(variables: JsonObject): string | null {
  const workspace = variables["workspace"]
  if (!workspace || typeof workspace !== "object" || Array.isArray(workspace)) return null
  const branch = (workspace as JsonObject)["branch"]
  return typeof branch === "string" && branch.length > 0 ? branch : null
}

export async function readCurrentBranch(workDir: string, signal: AbortSignal, log: TaskLogger | null = null): Promise<CurrentBranchResult> {
  const sink = branchCheckSink(log)
  const probe = await git(workDir, ["rev-parse", "--abbrev-ref", "HEAD"], signal, sink ? { sink } : undefined)
  if (!probe.success) {
    const stderr = (probe.stderr ?? "").toLowerCase()
    if (stderr.includes("not a git repository")) {
      return { branch: null, ref: null, detached: false, nonGit: true, error: null }
    }
    return { branch: null, ref: null, detached: false, nonGit: false, error: probe.combinedOutput || `exit ${probe.exitCode}` }
  }
  const branch = probe.stdout.trim()
  if (branch === "HEAD") {
    const refProbe = await git(workDir, ["rev-parse", "HEAD"], signal, sink ? { sink } : undefined)
    return { branch: null, ref: refProbe.success ? refProbe.stdout.trim() : null, detached: true, nonGit: false, error: null }
  }
  return { branch, ref: branch, detached: false, nonGit: false, error: null }
}

export function branchInvariantViolationFailure(
  work: DispatchWorkItem,
  evidence: BranchInvariantViolationEvidence,
): WorkItemResult {
  const label = work.title?.trim() || work.uses || work.workId
  const observed = evidence.observedBranch || `(detached at ${evidence.observedRef ?? "unknown"})`
  const detail = evidence.detail ? `; ${evidence.detail}` : ""
  const message = `branch-invariant violation at ${evidence.boundary} boundary for ${label}: ` +
    `expected branch '${evidence.expectedBranch}', observed '${observed}'${detail}`.slice(0, 4000)
  return {
    status: "failed",
    message,
    error: { code: "branch-invariant-violation", message },
  }
}

/**
 * Task boundary invariant: the workflow workspace must remain on
 * `workspace.branch` for the entire lifetime of a task. The start
 * check runs before the action is invoked; the end check runs after
 * a successful action but before `enforceCleanWorktree` so a
 * wrong-branch state is reported as a branch-invariant violation
 * (runner/action bug) rather than as a generic dirty-worktree
 * failure. The two checks are intentionally not exhaustive: the
 * action itself may temporarily move refs, and that is the
 * integration's contract; we only assert the boundary.
 */
export async function checkBranchStability(
  work: DispatchWorkItem,
  workDir: string,
  expectedBranch: string | null,
  boundary: "start" | "end",
  signal: AbortSignal,
  log: TaskLogger | null = null,
): Promise<
  | { kind: "ok"; evidence: BranchStabilityEvidence }
  | { kind: "violation"; result: WorkItemResult }
> {
  const observed = await readCurrentBranch(workDir, signal, log)
  if (expectedBranch === null) {
    const evidence: BranchStabilityEvidence = {
      kind: "branch-stability",
      boundary,
      expectedBranch: "",
      observedBranch: observed.branch ?? "",
      observedRef: observed.ref,
    }
    return { kind: "ok", evidence }
  }
  if (observed.nonGit) {
    const evidence: BranchStabilityEvidence = {
      kind: "branch-stability",
      boundary,
      expectedBranch,
      observedBranch: "",
      observedRef: null,
    }
    return { kind: "ok", evidence }
  }
  const evidence: BranchStabilityEvidence = {
    kind: "branch-stability",
    boundary,
    expectedBranch,
    observedBranch: observed.branch ?? "",
    observedRef: observed.ref,
  }
  if (observed.error) {
    return {
      kind: "violation",
      result: branchInvariantViolationFailure(work, {
        kind: "branch-invariant-violation",
        boundary,
        expectedBranch,
        observedBranch: observed.branch ?? "",
        observedRef: observed.ref,
        detail: `git rev-parse --abbrev-ref HEAD probe failed: ${observed.error}`,
      }),
    }
  }
  if (observed.detached) {
    return {
      kind: "violation",
      result: branchInvariantViolationFailure(work, {
        kind: "branch-invariant-violation",
        boundary,
        expectedBranch,
        observedBranch: "",
        observedRef: observed.ref,
      }),
    }
  }
  if (observed.branch !== expectedBranch) {
    return {
      kind: "violation",
      result: branchInvariantViolationFailure(work, {
        kind: "branch-invariant-violation",
        boundary,
        expectedBranch,
        observedBranch: observed.branch ?? "",
        observedRef: observed.ref,
      }),
    }
  }
  return { kind: "ok", evidence }
}
