import { join } from 'node:path'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceGitHandlers } from '../src/server/workspace-git-handlers.js'
import { resolveWorkspaceQuery } from '../src/runtime/workspace-query.js'
import { MemoryDirectoryHandleFileSystem } from './support/memory-filesystem.js'
import { withTestRunnerResources } from './support/test-resources.js'

const gitUrl = 'https://repo.test/mohist.git'

describe('workspace Git handler repository boundary', () => {
  it('rejects a repository replacement symlink before handler Git operations', async () => {
    const fileSystem = new MemoryDirectoryHandleFileSystem()
    const runnerRoot = '/runner'
    const workspacePath = join(runnerRoot, 'workspaces', 'wr-1')
    const repositoryPath = join(workspacePath, 'REPOS', 'main')
    const heldRepository = join(workspacePath, 'REPOS', 'main-held')
    const outside = '/outside/repository'
    await fileSystem.ensureDir(join(repositoryPath, '.git'))
    await fileSystem.ensureDir(outside)
    await fileSystem.writeText(
      join(workspacePath, '.mohist', 'workspace.json'),
      JSON.stringify({ workflowRunId: 'wr-1', runBranch: 'mohist/run-wr-1' }),
    )
    const handlerGit = vi.fn(async () => ({ exitCode: 0, stdout: 'true\n', stderr: '' }))
    let swapped = false

    const value = await withTestRunnerResources(
      async () => {
        const handlers = createWorkspaceGitHandlers({
          resolveQuery: resolveWorkspaceQuery,
          runnerRoot,
          runCommand: handlerGit,
        })
        return await handlers.getDiff({
          workflowRunId: 'wr-1',
          repositoryName: 'main',
          gitUrl,
          workspacePath,
          branch: 'mohist/run-wr-1',
          baseBranch: 'main',
        })
      },
      {
        fileSystem,
        commandRunner: {
          run: async (_command, args) => {
            if (args.includes('remote') && !swapped) {
              swapped = true
              await fileSystem.rename(repositoryPath, heldRepository)
              await fileSystem.symlink(outside, repositoryPath)
            }
            return { exitCode: 0, stdout: `${gitUrl}\n`, stderr: '' }
          },
        },
      },
    )

    expect(value).toBeNull()
    expect(handlerGit).not.toHaveBeenCalled()
    expect(fileSystem.exists(join(outside, '.git'))).toBe(false)
  })
})
