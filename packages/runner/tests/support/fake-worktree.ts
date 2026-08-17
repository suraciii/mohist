import { join } from "node:path"
import type { RunnerGitRunner, RunnerGitResult } from "../../src/system/filesystem.js"
import type { GitOptions } from "../../src/actions/git.js"

/**
 * Stateful fake Git worktree used by action-level regression tests
 * (`mohist/workspace-prepare`, and later `mohist/rebase`).
 *
 * Unlike fixed command-response stubs, the fake tracks the current branch
 * (or detached HEAD), worktree dirtiness, residual rebase / merge /
 * cherry-pick markers, and existing branches, and mutates that state as
 * commands execute. Assertions can therefore verify both the command
 * sequence AND the resulting workspace state, and transient command
 * failures can be injected to exercise repair boundaries.
 *
 * The fake plugs into `workspacePrepareGitRunner` for Git commands and
 * `workspacePrepareExistsChecker` for residual-marker existence probes.
 */
export interface FakeWorktreeResidual {
  rebaseMerge?: boolean
  rebaseApply?: boolean
  mergeHead?: boolean
  cherryPickHead?: boolean
}

/**
 * Simulated outcome of the `rebase <ref>` command. Git keeps HEAD on the
 * current branch after a successful rebase; `successBranch` lets tests
 * force a detached (`null`) or wrong-branch completion, and
 * `conflictFiles` simulates an unresolved conflict that leaves the rebase
 * in progress on a detached HEAD.
 */
export interface FakeRebaseSimulation {
  /** When false, `rebase <ref>` fails without leaving conflict markers. */
  succeeds?: boolean
  /** Branch HEAD is on after a successful rebase; null = detached. Undefined = keep the current branch. */
  successBranch?: string | null
  /** Commit sha after a successful rebase (default derived from the branch). */
  successCommit?: string
  /** When non-empty, `rebase <ref>` fails with these unresolved files and leaves rebase-merge residual + detached HEAD. */
  conflictFiles?: string[]
}

export interface FakeWorktreeState {
  /** Current branch name; null means detached. */
  branch: string | null
  /** Commit sha returned by `rev-parse HEAD` (also the detached ref). */
  commit?: string
  /** `status --porcelain` output; '' (absent) means clean. */
  porcelain?: string
  residual?: FakeWorktreeResidual
  /** Local branches that exist (checkout only succeeds for these). */
  branches?: string[]
  /** When false, `checkout <branch>` reports success but leaves HEAD unchanged. */
  checkoutAttaches?: boolean
  /** Extra refs resolved by `rev-parse <name>` (e.g. base refs). */
  revs?: Record<string, string>
}

export interface FakeWorktreeCall {
  workDir: string
  args: string[]
  timeoutMs: number | undefined
}

export type FakeGitResult = RunnerGitResult

const DEFAULT_RESIDUAL: Required<FakeWorktreeResidual> = {
  rebaseMerge: false,
  rebaseApply: false,
  mergeHead: false,
  cherryPickHead: false,
}

interface InternalState {
  branch: string | null
  commit: string
  porcelain: string
  residual: Required<FakeWorktreeResidual>
  branches: string[]
  checkoutAttaches: boolean
  revs: Record<string, string>
}

export class StatefulFakeWorktree {
  readonly calls: FakeWorktreeCall[] = []
  private readonly states = new Map<string, InternalState>()
  private readonly failures: Array<{ match: (args: string[]) => boolean; message: string; remaining: number }> = []
  /** When true, abort commands report success but leave the marker in place. */
  abortLeavesResidual = false
  /** When true, `reset --hard HEAD` / `clean -fd` report success but keep the dirt. */
  resetCleanIneffective = false
  /** Simulated outcome of the `rebase <ref>` command (undefined = keep the current branch on success). */
  rebaseSimulation?: FakeRebaseSimulation

  configure(workDir: string, state: FakeWorktreeState): void {
    this.states.set(workDir, {
      branch: state.branch,
      commit: state.commit ?? "clean-head-sha",
      porcelain: state.porcelain ?? "",
      residual: { ...DEFAULT_RESIDUAL, ...(state.residual ?? {}) },
      branches: [...(state.branches ?? [])],
      checkoutAttaches: state.checkoutAttaches ?? true,
      revs: { ...(state.revs ?? {}) },
    })
  }

  state(workDir: string): InternalState | undefined {
    return this.states.get(workDir)
  }

  hasCommand(...args: string[]): boolean {
    return this.calls.some((call) => call.args.join(" ") === args.join(" "))
  }

  commandCount(...args: string[]): number {
    return this.calls.filter((call) => call.args.join(" ") === args.join(" ")).length
  }

  fail(match: (args: string[]) => boolean, message: string, times = 1): void {
    this.failures.push({ match, message, remaining: times })
  }

  /** Residual-marker existence checker wired as `workspacePrepareExistsChecker`. */
  readonly existsChecker = (path: string): boolean => {
    for (const [workDir, state] of this.states) {
      const gitDir = join(workDir, ".git")
      const marker = path.startsWith(`${gitDir}/`) ? path.slice(gitDir.length + 1) : null
      if (marker === null) continue
      if (marker === "rebase-merge") return state.residual.rebaseMerge
      if (marker === "rebase-apply") return state.residual.rebaseApply
      if (marker === "MERGE_HEAD") return state.residual.mergeHead
      if (marker === "CHERRY_PICK_HEAD") return state.residual.cherryPickHead
      return false
    }
    return false
  }

  /** Git runner wired as `workspacePrepareGitRunner` (and the shared gitRunner). */
  readonly gitRunner: RunnerGitRunner = async (workDir, args, _signal, options) => {
    this.calls.push({ workDir, args: [...args], timeoutMs: options?.timeoutMs })
    const transient = this.failures.find((failure) => failure.match(args))
    if (transient) {
      transient.remaining -= 1
      if (transient.remaining <= 0) this.failures.splice(this.failures.indexOf(transient), 1)
      return failure(transient.message)
    }
    const state = this.states.get(workDir)
    if (!state) return failure(`unknown fake worktree: ${workDir}`)
    return await this.handle(workDir, args, state)
  }

  private async handle(workDir: string, args: string[], state: Required<FakeWorktreeState>): Promise<FakeGitResult> {
    const command = args.join(" ")
    if (args[0] === "rev-parse" && args[1] === "--git-path") {
      return ok(join(workDir, ".git", args[2] ?? "") + "\n")
    }
    if (command === "rev-parse HEAD") {
      return ok(`${state.commit}\n`)
    }
    if (command === "rev-parse --abbrev-ref HEAD") {
      return ok(state.branch === null ? "HEAD\n" : `${state.branch}\n`)
    }
    if (command === "status --porcelain") {
      return ok(state.porcelain)
    }
    if (command === "rebase --abort") {
      if (!this.abortLeavesResidual) {
        state.residual.rebaseMerge = false
        state.residual.rebaseApply = false
      }
      return ok("Aborted rebase\n")
    }
    if (command === "merge --abort") {
      if (!this.abortLeavesResidual) state.residual.mergeHead = false
      return ok("Merge aborted\n")
    }
    if (command === "cherry-pick --abort") {
      if (!this.abortLeavesResidual) state.residual.cherryPickHead = false
      return ok("Cherry-pick aborted\n")
    }
    if (command === "reset --hard HEAD") {
      if (!this.resetCleanIneffective) state.porcelain = ""
      return ok("HEAD is now at clean-head-sha\n")
    }
    if (command === "clean -fd") {
      if (!this.resetCleanIneffective) state.porcelain = ""
      return ok("Removing untracked files\n")
    }
    if (command === "diff --name-only --diff-filter=U") {
      const files = this.rebaseSimulation?.conflictFiles ?? []
      return ok(files.length ? `${files.join("\n")}\n` : "")
    }
    if (command === "add .") {
      state.porcelain = ""
      return ok("")
    }
    if (args[0] === "commit") {
      state.porcelain = ""
      return ok(`[${state.branch ?? "detached"} abc123] ${args.slice(2).join(" ")}\n`)
    }
    if (args[0] === "reset" && args[1] === "--soft") {
      return ok("")
    }
    if (args[0] === "fetch") {
      return ok("From https://example.com/repo\n * branch -> FETCH_HEAD")
    }
    if (args[0] === "rebase" && args[1] !== "--abort") {
      return this.handleRebase(state)
    }
    if (args[0] === "rev-parse" && args[1] !== "--git-path" && args[1] !== "HEAD" && args[1] !== "--abbrev-ref") {
      const sha = state.revs[args[1] ?? ""]
      return sha ? ok(`${sha}\n`) : failure(`fatal: ambiguous argument '${args[1]}'`)
    }
    if (args[0] === "checkout") {
      const branch = args[1]
      if (!branch) return failure("checkout requires a branch")
      if (state.checkoutAttaches && !state.branches.includes(branch)) {
        return failure(`error: pathspec '${branch}' did not match any file(s) known to git`)
      }
      if (state.checkoutAttaches) {
        state.branch = branch
        state.commit = `${branch}-head-sha`
      }
      return ok(`Switched to branch '${branch}'\n`)
    }
    return failure(`unexpected git call: ${command}`)
  }

  private handleRebase(state: Required<FakeWorktreeState>): FakeGitResult {
    const sim = this.rebaseSimulation
    if (sim?.conflictFiles && sim.conflictFiles.length > 0) {
      state.residual.rebaseMerge = true
      state.branch = null
      state.commit = "conflict-head-sha"
      return failure(`CONFLICT (content): Merge conflict in ${sim.conflictFiles[0]}`)
    }
    if (sim?.succeeds === false) {
      return failure("fatal: rebase failed")
    }
    if (sim && "successBranch" in sim) {
      state.branch = sim.successBranch ?? null
      state.commit = sim.successCommit ?? (state.branch === null ? "detached-after-rebase" : `${state.branch}-rebase-head-sha`)
    }
    return ok("Successfully rebased and updated refs/heads/mo/issue-217.")
  }
}

function ok(stdout: string): FakeGitResult {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function failure(stderr: string): FakeGitResult {
  return { success: false, stdout: "", stderr, exitCode: 1, combinedOutput: stderr }
}

export function gitOptionsFor(fake: StatefulFakeWorktree): { workspacePrepareGitRunner: RunnerGitRunner; workspacePrepareExistsChecker: (path: string) => boolean } {
  return {
    workspacePrepareGitRunner: fake.gitRunner,
    workspacePrepareExistsChecker: fake.existsChecker,
  }
}

export type { GitOptions }
