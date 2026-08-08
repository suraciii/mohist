import { git as defaultGit, type GitOptions } from "../actions/git.js"
import { currentRunnerResources } from "../system/filesystem.js"

export type GitRunner = import("../system/filesystem.js").RunnerGitRunner

export async function git(workDir: string, args: string[], signal: AbortSignal, options?: GitOptions) {
  const runner = currentRunnerResources()?.gitRunner ?? defaultGit
  return await runner(workDir, args, signal, options)
}

export type { GitOptions }
