import { join } from "node:path"
import type { ActionContext, ActionResult } from "../core/types.js"
import { stringAt } from "../core/json-path.js"
import { exists } from "../system/process.js"
import { git as defaultGit } from "./git.js"

type GitRunner = typeof defaultGit
type ExistsChecker = typeof exists
type GitResult = Awaited<ReturnType<GitRunner>>

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
}

const DETACHED_REF = "(detached)"

export async function workspacePrepareAction(context: ActionContext): Promise<ActionResult> {
  const expectedBranch = stringAt(context.variables, ["workspace", "branch"])
  const workDir = stringAt(context.variables, ["workspace", "path"]) ?? context.workDir

  if (!expectedBranch) {
    const snapshot = await captureSnapshot(workDir, context.signal)
    return failureOutput(workDir, "(none)", snapshot, "resolve", "Workspace branch is not defined in context.variables.workspace.branch", 1)
  }

  const initial = await captureSnapshot(workDir, context.signal)

  if (isCleanAndAligned(initial, expectedBranch)) {
    return successOutput(workDir, expectedBranch, initial)
  }

  if (initial.residual.rebaseMerge || initial.residual.rebaseApply) {
    const abort = await git(workDir, ["rebase", "--abort"], context.signal)
    if (!abort.success) {
      const after = await captureSnapshot(workDir, context.signal)
      return failureOutput(workDir, expectedBranch, after, "abort-rebase", `git rebase --abort failed: ${abort.combinedOutput}`, abort.exitCode)
    }
    const reprobe = await probeRebaseDirs(workDir, context.signal)
    if (reprobe.rebaseMerge || reprobe.rebaseApply) {
      const after = await captureSnapshot(workDir, context.signal)
      return failureOutput(workDir, expectedBranch, after, "abort-rebase", "Rebase is still in progress after abort", 1)
    }
  }

  if (initial.residual.mergeHead) {
    const abort = await git(workDir, ["merge", "--abort"], context.signal)
    if (!abort.success) {
      const after = await captureSnapshot(workDir, context.signal)
      return failureOutput(workDir, expectedBranch, after, "abort-merge", `git merge --abort failed: ${abort.combinedOutput}`, abort.exitCode)
    }
    if (await probeMergeHead(workDir, context.signal)) {
      const after = await captureSnapshot(workDir, context.signal)
      return failureOutput(workDir, expectedBranch, after, "abort-merge", "Merge is still in progress after abort", 1)
    }
  }

  if (initial.residual.cherryPickHead) {
    const abort = await git(workDir, ["cherry-pick", "--abort"], context.signal)
    if (!abort.success) {
      const after = await captureSnapshot(workDir, context.signal)
      return failureOutput(workDir, expectedBranch, after, "abort-cherry-pick", `git cherry-pick --abort failed: ${abort.combinedOutput}`, abort.exitCode)
    }
    if (await probeCherryPickHead(workDir, context.signal)) {
      const after = await captureSnapshot(workDir, context.signal)
      return failureOutput(workDir, expectedBranch, after, "abort-cherry-pick", "Cherry-pick is still in progress after abort", 1)
    }
  }

  const currentRef = await readHeadRef(workDir, context.signal)
  if (currentRef !== expectedBranch) {
    const checkout = await git(workDir, ["checkout", expectedBranch], context.signal)
    if (!checkout.success) {
      const after = await captureSnapshot(workDir, context.signal)
      return failureOutput(workDir, expectedBranch, after, "checkout", `git checkout ${expectedBranch} failed: ${checkout.combinedOutput}`, checkout.exitCode)
    }
  }

  const porcelain = await git(workDir, ["status", "--porcelain"], context.signal)
  if (!porcelain.success) {
    const after = await captureSnapshot(workDir, context.signal)
    return failureOutput(workDir, expectedBranch, after, "status", `git status --porcelain failed: ${porcelain.combinedOutput}`, porcelain.exitCode)
  }
  if (porcelain.stdout.trim() !== "") {
    const reset = await git(workDir, ["reset", "--hard", "HEAD"], context.signal)
    if (!reset.success) {
      const after = await captureSnapshot(workDir, context.signal)
      return failureOutput(workDir, expectedBranch, after, "reset", `git reset --hard HEAD failed: ${reset.combinedOutput}`, reset.exitCode)
    }
    const clean = await git(workDir, ["clean", "-fd"], context.signal)
    if (!clean.success) {
      const after = await captureSnapshot(workDir, context.signal)
      return failureOutput(workDir, expectedBranch, after, "clean", `git clean -fd failed: ${clean.combinedOutput}`, clean.exitCode)
    }
  }

  const verify = await captureSnapshot(workDir, context.signal)
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

async function captureSnapshot(workDir: string, signal: AbortSignal): Promise<WorkspaceSnapshot> {
  const [residual, head, porcelainResult] = await Promise.all([
    probeResidual(workDir, signal),
    captureHead(workDir, signal),
    git(workDir, ["status", "--porcelain"], signal),
  ])
  const porcelain = porcelainResult.success ? porcelainResult.stdout : ""
  return { residual, head, porcelain }
}

async function captureHead(workDir: string, signal: AbortSignal): Promise<HeadState> {
  const [headResult, refResult] = await Promise.all([
    git(workDir, ["rev-parse", "HEAD"], signal),
    git(workDir, ["rev-parse", "--abbrev-ref", "HEAD"], signal),
  ])
  const commit = headResult.success ? headResult.stdout.trim() : ""
  let ref = DETACHED_REF
  if (refResult.success) {
    const trimmed = refResult.stdout.trim()
    if (trimmed !== "" && trimmed !== "HEAD") ref = trimmed
  }
  return { commit, ref }
}

async function readHeadRef(workDir: string, signal: AbortSignal): Promise<string> {
  const refResult = await git(workDir, ["rev-parse", "--abbrev-ref", "HEAD"], signal)
  if (!refResult.success) return DETACHED_REF
  const trimmed = refResult.stdout.trim()
  return trimmed === "" || trimmed === "HEAD" ? DETACHED_REF : trimmed
}

async function probeResidual(workDir: string, signal: AbortSignal): Promise<ResidualState> {
  const [rebaseMerge, rebaseApply, mergeHead, cherryPickHead] = await Promise.all([
    probePathExists(workDir, "rebase-merge", signal),
    probePathExists(workDir, "rebase-apply", signal),
    probePathExists(workDir, "MERGE_HEAD", signal),
    probePathExists(workDir, "CHERRY_PICK_HEAD", signal),
  ])
  return { rebaseMerge, rebaseApply, mergeHead, cherryPickHead }
}

async function probeRebaseDirs(workDir: string, signal: AbortSignal): Promise<{ rebaseMerge: boolean; rebaseApply: boolean }> {
  const [rebaseMerge, rebaseApply] = await Promise.all([
    probePathExists(workDir, "rebase-merge", signal),
    probePathExists(workDir, "rebase-apply", signal),
  ])
  return { rebaseMerge, rebaseApply }
}

async function probeMergeHead(workDir: string, signal: AbortSignal): Promise<boolean> {
  return await probePathExists(workDir, "MERGE_HEAD", signal)
}

async function probeCherryPickHead(workDir: string, signal: AbortSignal): Promise<boolean> {
  return await probePathExists(workDir, "CHERRY_PICK_HEAD", signal)
}

async function probePathExists(workDir: string, gitPath: string, signal: AbortSignal): Promise<boolean> {
  const result = await git(workDir, ["rev-parse", "--git-path", gitPath], signal)
  if (!result.success) return false
  return pathExists(resolveGitPath(workDir, result.stdout.trim()))
}

function resolveGitPath(workDir: string, path: string): string {
  return path.match(/^[A-Za-z]:[\\/]|^\//) ? path : join(workDir, path)
}

function isCleanAndAligned(snapshot: WorkspaceSnapshot, expectedBranch: string): boolean {
  const cleanResidual = !snapshot.residual.rebaseMerge
    && !snapshot.residual.rebaseApply
    && !snapshot.residual.mergeHead
    && !snapshot.residual.cherryPickHead
  const aligned = snapshot.head.ref === expectedBranch
  const cleanTree = snapshot.porcelain.trim() === ""
  return cleanResidual && aligned && cleanTree
}

function successOutput(workDir: string, expectedBranch: string, snapshot: WorkspaceSnapshot): ActionResult {
  const output = JSON.stringify({
    kind: "workspace-prepare",
    status: "success",
    failureKind: null,
    expectedBranch,
    head: snapshot.head,
    residual: snapshot.residual,
    porcelain: snapshot.porcelain,
    step: null,
    workDir,
  })
  return { status: "success", message: "Workspace prepared", output, exitCode: 0 }
}

function failureOutput(
  workDir: string,
  expectedBranch: string,
  snapshot: WorkspaceSnapshot,
  step: string,
  message: string,
  exitCode: number | null,
): ActionResult {
  const output = JSON.stringify({
    kind: "workspace-prepare",
    status: "failure",
    failureKind: "workspace-setup",
    expectedBranch,
    head: snapshot.head,
    residual: snapshot.residual,
    porcelain: snapshot.porcelain,
    step,
    workDir,
  })
  return { status: "failure", message, output, exitCode: exitCode ?? 1 }
}
