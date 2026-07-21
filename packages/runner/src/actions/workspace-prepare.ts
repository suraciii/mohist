import { join } from "node:path"
import type { ActionResult, JsonObject } from "../core/types.js"
import type { ActionInvocationContext } from "./context.js"
import { stringInput } from "../core/json.js"
import { exists } from "../system/process.js"
import { git as defaultGit, type GitOptions } from "./git.js"
import { fail, succeed } from "./action-result.js"

type GitRunner = (workDir: string, args: string[], signal: AbortSignal, options?: GitOptions) => Promise<{
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
}>
type ExistsChecker = typeof exists
type GitResult = Awaited<ReturnType<GitRunner>>

/**
 * `source` tag recorded against every captured `mohist/workspace-prepare`
 * action body line. Distinct from `workspace-prep` (the clone/checkout
 * phase that runs as part of the executor lifecycle) so the web viewer
 * phase-distinguishes the action body from the dispatcher-level
 * workspace materialization.
 */
const ACTION_SOURCE = "action:workspace-prepare"

let git: GitRunner = defaultGit
let pathExists: ExistsChecker = exists

export type WorkspacePrepareGitResult = GitResult

export function setWorkspacePrepareGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setWorkspacePrepareExistsCheckerForTest(checker: ExistsChecker | null) {
  pathExists = checker ?? exists
}

interface ResidualState {
  rebaseMerge: boolean
  rebaseApply: boolean
  mergeHead: boolean
  cherryPickHead: boolean
}

interface HeadState {
  commit: string
  ref: string
}

interface WorkspaceSnapshot {
  residual: ResidualState
  head: HeadState
  porcelain: string
  probeFailure: ProbeFailure | null
}

interface ProbeFailure {
  step: string
  message: string
  exitCode: number | null
}

interface ResidualProbe {
  residual: ResidualState
  failure: ProbeFailure | null
}

interface PathProbe {
  exists: boolean
  failure: ProbeFailure | null
}

interface HeadProbe {
  head: HeadState
  failure: ProbeFailure | null
}

const DETACHED_REF = "(detached)"

function sinkOptions(context: ActionInvocationContext): GitOptions | undefined {
  return context.log ? { sink: { log: context.log, source: ACTION_SOURCE } } : undefined
}

export async function workspacePrepareAction(context: ActionInvocationContext): Promise<ActionResult> {
  const expectedBranch = stringInput(context.with, "expectedBranch")
  const workDir = context.workDir
  const opts = sinkOptions(context)

  if (!expectedBranch) {
    const snapshot = await captureSnapshot(workDir, context.signal, opts)
    return failureOutput(workDir, "(none)", snapshot, "resolve", "Workspace branch is not defined in with.expectedBranch", 1)
  }

  const initial = await captureSnapshot(workDir, context.signal, opts)
  const initialProbeFailure = probeFailureOutput(workDir, expectedBranch, initial)
  if (initialProbeFailure) return initialProbeFailure

  if (isCleanAndAligned(initial, expectedBranch)) {
    return successOutput(workDir, expectedBranch, initial)
  }

  let current = initial

  if (current.residual.rebaseMerge || current.residual.rebaseApply) {
    const abort = await git(workDir, ["rebase", "--abort"], context.signal, opts)
    if (!abort.success) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "abort-rebase", `git rebase --abort failed: ${abort.combinedOutput}`, abort.exitCode)
    }
    const reprobe = await probeRebaseDirs(workDir, context.signal, opts)
    if (reprobe.failure) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "abort-rebase", reprobe.failure.message, reprobe.failure.exitCode)
    }
    if (reprobe.rebaseMerge || reprobe.rebaseApply) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "abort-rebase", "Rebase is still in progress after abort", 1)
    }
    current = await captureSnapshot(workDir, context.signal, opts)
    const currentProbeFailure = probeFailureOutput(workDir, expectedBranch, current)
    if (currentProbeFailure) return currentProbeFailure
  }

  if (current.residual.mergeHead) {
    const abort = await git(workDir, ["merge", "--abort"], context.signal, opts)
    if (!abort.success) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "abort-merge", `git merge --abort failed: ${abort.combinedOutput}`, abort.exitCode)
    }
    const reprobe = await probeMergeHead(workDir, context.signal, opts)
    if (reprobe.failure) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "abort-merge", reprobe.failure.message, reprobe.failure.exitCode)
    }
    if (reprobe.exists) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "abort-merge", "Merge is still in progress after abort", 1)
    }
    current = await captureSnapshot(workDir, context.signal, opts)
    const currentProbeFailure = probeFailureOutput(workDir, expectedBranch, current)
    if (currentProbeFailure) return currentProbeFailure
  }

  if (current.residual.cherryPickHead) {
    const abort = await git(workDir, ["cherry-pick", "--abort"], context.signal, opts)
    if (!abort.success) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "abort-cherry-pick", `git cherry-pick --abort failed: ${abort.combinedOutput}`, abort.exitCode)
    }
    const reprobe = await probeCherryPickHead(workDir, context.signal, opts)
    if (reprobe.failure) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "abort-cherry-pick", reprobe.failure.message, reprobe.failure.exitCode)
    }
    if (reprobe.exists) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "abort-cherry-pick", "Cherry-pick is still in progress after abort", 1)
    }
    current = await captureSnapshot(workDir, context.signal, opts)
    const currentProbeFailure = probeFailureOutput(workDir, expectedBranch, current)
    if (currentProbeFailure) return currentProbeFailure
  }

  if (current.porcelain.trim() !== "") {
    const reset = await git(workDir, ["reset", "--hard", "HEAD"], context.signal, opts)
    if (!reset.success) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "reset", `git reset --hard HEAD failed: ${reset.combinedOutput}`, reset.exitCode)
    }
    const clean = await git(workDir, ["clean", "-fd"], context.signal, opts)
    if (!clean.success) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "clean", `git clean -fd failed: ${clean.combinedOutput}`, clean.exitCode)
    }
    current = await captureSnapshot(workDir, context.signal, opts)
    const currentProbeFailure = probeFailureOutput(workDir, expectedBranch, current)
    if (currentProbeFailure) return currentProbeFailure
  }

  if (current.head.ref !== expectedBranch) {
    const checkout = await git(workDir, ["checkout", expectedBranch], context.signal, opts)
    if (!checkout.success) {
      const after = await captureSnapshot(workDir, context.signal, opts)
      return failureOutput(workDir, expectedBranch, after, "checkout", `git checkout ${expectedBranch} failed: ${checkout.combinedOutput}`, checkout.exitCode)
    }
  }

  const verify = await captureSnapshot(workDir, context.signal, opts)
  const verifyProbeFailure = probeFailureOutput(workDir, expectedBranch, verify)
  if (verifyProbeFailure) return verifyProbeFailure
  if (verify.residual.rebaseMerge || verify.residual.rebaseApply) {
    return failureOutput(workDir, expectedBranch, verify, "verify", "Health verification failed: rebase directory still present after cleanup", 1)
  }
  if (verify.residual.mergeHead) {
    return failureOutput(workDir, expectedBranch, verify, "verify", "Health verification failed: merge still in progress after cleanup", 1)
  }
  if (verify.residual.cherryPickHead) {
    return failureOutput(workDir, expectedBranch, verify, "verify", "Health verification failed: cherry-pick still in progress after cleanup", 1)
  }
  if (verify.head.ref !== expectedBranch) {
    return failureOutput(workDir, expectedBranch, verify, "verify", `Health verification failed: HEAD not on expected branch (current: ${verify.head.ref}, expected: ${expectedBranch})`, 1)
  }
  if (verify.porcelain.trim() !== "") {
    return failureOutput(workDir, expectedBranch, verify, "verify", "Health verification failed: working tree still dirty after cleanup", 1)
  }

  return successOutput(workDir, expectedBranch, verify)
}

async function captureSnapshot(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<WorkspaceSnapshot> {
  const [residualProbe, headProbe, porcelainResult] = await Promise.all([
    probeResidual(workDir, signal, opts),
    captureHead(workDir, signal, opts),
    git(workDir, ["status", "--porcelain"], signal, opts),
  ])
  const statusFailure = porcelainResult.success
    ? null
    : gitFailure("status", "git status --porcelain", porcelainResult)
  const porcelain = porcelainResult.success ? porcelainResult.stdout : ""
  return {
    residual: residualProbe.residual,
    head: headProbe.head,
    porcelain,
    probeFailure: residualProbe.failure ?? headProbe.failure ?? statusFailure,
  }
}

async function captureHead(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<HeadProbe> {
  const [headResult, refResult] = await Promise.all([
    git(workDir, ["rev-parse", "HEAD"], signal, opts),
    git(workDir, ["rev-parse", "--abbrev-ref", "HEAD"], signal, opts),
  ])
  const commit = headResult.success ? headResult.stdout.trim() : ""
  let ref = DETACHED_REF
  if (refResult.success) {
    const trimmed = refResult.stdout.trim()
    if (trimmed !== "" && trimmed !== "HEAD") ref = trimmed
  }
  const failure = !headResult.success
    ? gitFailure("head", "git rev-parse HEAD", headResult)
    : !refResult.success
      ? gitFailure("head-ref", "git rev-parse --abbrev-ref HEAD", refResult)
      : null
  return { head: { commit, ref }, failure }
}

async function probeResidual(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<ResidualProbe> {
  const [rebaseMerge, rebaseApply, mergeHead, cherryPickHead] = await Promise.all([
    probePathExists(workDir, "rebase-merge", signal, opts),
    probePathExists(workDir, "rebase-apply", signal, opts),
    probePathExists(workDir, "MERGE_HEAD", signal, opts),
    probePathExists(workDir, "CHERRY_PICK_HEAD", signal, opts),
  ])
  return {
    residual: {
      rebaseMerge: rebaseMerge.exists,
      rebaseApply: rebaseApply.exists,
      mergeHead: mergeHead.exists,
      cherryPickHead: cherryPickHead.exists,
    },
    failure: rebaseMerge.failure ?? rebaseApply.failure ?? mergeHead.failure ?? cherryPickHead.failure,
  }
}

async function probeRebaseDirs(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<{ rebaseMerge: boolean; rebaseApply: boolean; failure: ProbeFailure | null }> {
  const [rebaseMerge, rebaseApply] = await Promise.all([
    probePathExists(workDir, "rebase-merge", signal, opts),
    probePathExists(workDir, "rebase-apply", signal, opts),
  ])
  return { rebaseMerge: rebaseMerge.exists, rebaseApply: rebaseApply.exists, failure: rebaseMerge.failure ?? rebaseApply.failure }
}

async function probeMergeHead(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<PathProbe> {
  return await probePathExists(workDir, "MERGE_HEAD", signal, opts)
}

async function probeCherryPickHead(workDir: string, signal: AbortSignal, opts?: GitOptions): Promise<PathProbe> {
  return await probePathExists(workDir, "CHERRY_PICK_HEAD", signal, opts)
}

async function probePathExists(workDir: string, gitPath: string, signal: AbortSignal, opts?: GitOptions): Promise<PathProbe> {
  const result = await git(workDir, ["rev-parse", "--git-path", gitPath], signal, opts)
  if (!result.success) {
    return { exists: false, failure: gitFailure("residual", `git rev-parse --git-path ${gitPath}`, result) }
  }
  return { exists: pathExists(resolveGitPath(workDir, result.stdout.trim())), failure: null }
}

function resolveGitPath(workDir: string, path: string): string {
  return path.match(/^[A-Za-z]:[\\/]|^\//) ? path : join(workDir, path)
}

function isCleanAndAligned(snapshot: WorkspaceSnapshot, expectedBranch: string): boolean {
  if (snapshot.probeFailure) return false
  const cleanResidual = !snapshot.residual.rebaseMerge
    && !snapshot.residual.rebaseApply
    && !snapshot.residual.mergeHead
    && !snapshot.residual.cherryPickHead
  const aligned = snapshot.head.ref === expectedBranch
  const cleanTree = snapshot.porcelain.trim() === ""
  return cleanResidual && aligned && cleanTree
}

function probeFailureOutput(workDir: string, expectedBranch: string, snapshot: WorkspaceSnapshot): ActionResult | null {
  if (!snapshot.probeFailure) return null
  return failureOutput(
    workDir,
    expectedBranch,
    snapshot,
    snapshot.probeFailure.step,
    snapshot.probeFailure.message,
    snapshot.probeFailure.exitCode,
  )
}

function gitFailure(step: string, command: string, result: GitResult): ProbeFailure {
  return {
    step,
    message: `${command} failed: ${result.combinedOutput || `exit ${result.exitCode}`}`,
    exitCode: result.exitCode,
  }
}

function successOutput(workDir: string, expectedBranch: string, snapshot: WorkspaceSnapshot): ActionResult {
  const output: JsonObject = {
    kind: "workspace-prepare",
    status: "success",
    expectedBranch,
    head: snapshot.head as unknown as JsonObject,
    residual: snapshot.residual as unknown as JsonObject,
    porcelain: snapshot.porcelain,
    step: null,
    workDir,
  }
  return succeed(output, { exitCode: 0 })
}

function failureOutput(
  workDir: string,
  expectedBranch: string,
  snapshot: WorkspaceSnapshot,
  step: string,
  message: string,
  exitCode: number | null,
): ActionResult {
  void workDir
  void expectedBranch
  void snapshot
  void step
  return fail("workspace-setup", message, { exitCode: exitCode ?? 1 })
}
