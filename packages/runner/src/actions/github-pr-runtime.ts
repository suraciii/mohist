import { runCommand, type CommandLineOptions } from "../system/process.js"
import { git as defaultGit } from "./git.js"
import { combinedGhOutput } from "./github-pr-parse.js"

type GitRunner = typeof defaultGit
type GhRunner = typeof runCommand

let git: GitRunner = defaultGit
let gh: GhRunner = runCommand

export function getGitHubPrGit(): GitRunner {
  return git
}

export function getGitHubPrGh(): GhRunner {
  return gh
}

export function setGitHubPrGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export function setGitHubPrGhRunnerForTest(runner: GhRunner | null) {
  gh = runner ?? runCommand
}

export type GhPrecheckOk = { ok: true; output: string }
export type GhPrecheckFailure = { ok: false; exitCode: number; output: string; message: string }
export type GhPrecheckResult = GhPrecheckOk | GhPrecheckFailure

export async function runGhPrecheck(gh: GhRunner, workDir: string, signal: AbortSignal, options?: CommandLineOptions): Promise<GhPrecheckResult> {
  const version = await gh("gh", ["--version"], workDir, signal, undefined, options)
  if (version.exitCode !== 0) {
    const output = combinedGhOutput(version)
    return {
      ok: false,
      exitCode: version.exitCode,
      output,
      message: "gh CLI is not installed or not on PATH. Install GitHub CLI and run `gh auth login` on the runner host before re-running this issue.",
    }
  }

  const auth = await gh("gh", ["auth", "status"], workDir, signal, undefined, options)
  const authOutput = combinedGhOutput(auth)
  if (auth.exitCode !== 0) {
    return {
      ok: false,
      exitCode: auth.exitCode,
      output: authOutput,
      message: "gh CLI is installed but `gh auth status` did not return a logged-in account. Run `gh auth login` on the runner host before re-running this issue.",
    }
  }

  return { ok: true, output: `${version.stdout.trim()}\n${authOutput}` }
}

export type { GitRunner, GhRunner }
