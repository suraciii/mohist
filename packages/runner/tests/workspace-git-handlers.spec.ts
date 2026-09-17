import { join } from 'node:path'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceGitHandlers } from '../src/server/workspace-git-handlers.js'
import { resolveWorkspaceQuery } from '../src/runtime/workspace-query.js'
import { namedWorkspacePath } from '../src/runtime/workspace-entity.js'
import { MemoryDirectoryHandleFileSystem } from './support/memory-filesystem.js'
import { withTestRunnerResources } from './support/test-resources.js'

const gitUrl = 'https://repo.test/mohist.git'
const projectId = 'project-1'
const workspaceName = 'issue-1'
const runnerRoot = '/runner'

function namedQuery() {
  return {
    projectId,
    workspaceName,
    issueNumber: 1,
    repositoryName: 'main',
    gitUrl,
    branch: `mohist/ws-${workspaceName}`,
    baseBranch: 'main',
  }
}

async function writeMarker(fileSystem: MemoryDirectoryHandleFileSystem, markerWorkspaceName: string) {
  const workspacePath = namedWorkspacePath(runnerRoot, projectId, workspaceName)
  await fileSystem.ensureDir(join(workspacePath, 'REPOS', 'main', '.git'))
  await fileSystem.writeText(
    join(workspacePath, '.mohist', 'workspace.json'),
    JSON.stringify({
      projectId,
      workspaceName: markerWorkspaceName,
      repositories: [{ name: 'main', gitUrl }],
    }),
  )
  return workspacePath
}

describe('workspace Git handler named workspace boundary', () => {
  it('rejects a repository replacement symlink before handler Git operations', async () => {
    const fileSystem = new MemoryDirectoryHandleFileSystem()
    const workspacePath = await writeMarker(fileSystem, workspaceName)
    const repositoryPath = join(workspacePath, 'REPOS', 'main')
    const heldRepository = join(workspacePath, 'REPOS', 'main-held')
    const outside = '/outside/repository'
    await fileSystem.ensureDir(outside)
    const handlerGit = vi.fn(async () => ({ exitCode: 0, stdout: 'true\n', stderr: '' }))
    let swapped = false

    const value = await withTestRunnerResources(
      async () => {
        const handlers = createWorkspaceGitHandlers({
          resolveQuery: resolveWorkspaceQuery,
          runnerRoot,
          runCommand: handlerGit,
        })
        return await handlers.getDiff(namedQuery())
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

  it('refuses a named workspace whose marker names a different workspace', async () => {
    const fileSystem = new MemoryDirectoryHandleFileSystem()
    await writeMarker(fileSystem, 'issue-2')
    const handlerGit = vi.fn(async () => ({ exitCode: 0, stdout: 'true\n', stderr: '' }))

    const value = await withTestRunnerResources(
      async () => {
        const handlers = createWorkspaceGitHandlers({
          resolveQuery: resolveWorkspaceQuery,
          runnerRoot,
          runCommand: handlerGit,
        })
        return await handlers.getDiff(namedQuery())
      },
      {
        fileSystem,
        commandRunner: { run: async () => ({ exitCode: 0, stdout: `${gitUrl}\n`, stderr: '' }) },
      },
    )

    expect(value).toBeNull()
    expect(handlerGit).not.toHaveBeenCalled()
  })
})
