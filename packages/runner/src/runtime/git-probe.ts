import { git as defaultGit } from "../actions/git.js"

export type GitRunner = (workDir: string, args: string[], signal: AbortSignal) => Promise<{
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
}>

let git: GitRunner = defaultGit

export function setExecutorGitRunnerForTest(runner: GitRunner | null) {
  git = runner ?? defaultGit
}

export { git }
