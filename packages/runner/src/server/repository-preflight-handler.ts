import { NETWORK_COMMAND_TIMEOUT_MS } from '../actions/git.js'
import { currentRunnerResources } from '../system/filesystem.js'
import { runCommand as defaultRunCommand } from '../system/process.js'

export interface RepositoryPreflightParams {
  gitUrl: string
  baseBranch: string
}

export interface RepositoryPreflightHandlerDeps {
  runCommand?: typeof defaultRunCommand
}

/** Check a remote branch without materializing a workspace or exposing Git output. */
export function createRepositoryPreflightHandler(deps: RepositoryPreflightHandlerDeps = {}) {
  return async ({ gitUrl, baseBranch }: RepositoryPreflightParams): Promise<{ exitCode: number }> => {
    // Unknown URL schemes execute git-remote-* helpers; never hand them to Git.
    if (
      gitUrl.includes('::') ||
      (gitUrl.includes('://') &&
        !/^https:\/\//i.test(gitUrl) &&
        !/^ssh:\/\//i.test(gitUrl) &&
        !/^git:\/\//i.test(gitUrl))
    )
      return { exitCode: -1 }

    const controller = new AbortController()
    const runCommand = deps.runCommand ?? currentRunnerResources()?.controlGitRunner ?? defaultRunCommand
    try {
      const result = await runCommand(
        'git',
        [
          '-c',
          'protocol.ext.allow=never',
          'ls-remote',
          '--heads',
          '--exit-code',
          '--',
          gitUrl,
          `refs/heads/${baseBranch}`,
        ],
        '.',
        controller.signal,
        undefined,
        { timeoutMs: NETWORK_COMMAND_TIMEOUT_MS },
      )
      return { exitCode: result.status === 'timeout' ? 124 : result.exitCode }
    } catch {
      return { exitCode: -1 }
    }
  }
}
