import { join } from 'node:path'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { createWorkspaceRemovalHandler } from '../../src/server/workspace-removal-handler.js'
import { WorkspaceManager } from '../../src/runtime/workspace.js'
import {
  NamedWorkspaceRegistry,
  WorkspaceRegistry,
  defaultWorkspaceRegistryFilePath,
} from '../../src/runtime/workspace-registry.js'
import { namedWorkspacePath } from '../../src/runtime/workspace-entity.js'
import type { WorkspaceRemovalFence, WorkspaceRemovalFenceResult } from '../../src/runtime/workspace-removal-fence.js'
import type { RunnerFileSystem, RunnerResourceContext } from '../../src/system/filesystem.js'
import { MemoryFileSystem } from '../support/memory-filesystem.js'
import { withTestRunnerResources } from '../support/test-resources.js'

const testGitUrl = 'https://repo.test/mohist.git'
type TestResources = {
  fileSystem: RunnerFileSystem
  commandRunner: NonNullable<RunnerResourceContext['commandRunner']>
  controlExistsChecker: (path: string) => boolean
}

function it(name: string, body: (resources: TestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const fileSystem = new MemoryFileSystem()
    const resources = {
      fileSystem,
      commandRunner: undefined as unknown as TestResources['commandRunner'],
      controlExistsChecker: (path: string) => fileSystem.exists(path),
    }
    installGitFake(resources, new Map())
    await withTestRunnerResources(async () => await body(resources), resources)
  })
}

function commandResult(stdout = '') {
  return { exitCode: 0, stdout, stderr: '' }
}

function installGitFake(resources: TestResources, workspaceBranches: Map<string, string>) {
  const preparingSibling = (path: string) => {
    const marker = '/REPOS/'
    const index = path.indexOf(marker)
    return index >= 0 ? `${path.slice(0, index)}.preparing${path.slice(index)}` : `${path}.preparing`
  }
  resources.commandRunner = {
    async run(command, args) {
      if (command !== 'git') return commandResult()

      const workDir = args[0] === '-C' ? args[1] : null
      const gitArgs = workDir ? args.slice(2) : args

      if (gitArgs[0] === 'ls-remote') return commandResult('deadbeef\trefs/heads/main\n')

      if (gitArgs[0] === 'clone') {
        const workspacePath = gitArgs.at(-1)!
        await resources.fileSystem.ensureDir(join(workspacePath, '.git', 'info'))
        return commandResult()
      }

      if (gitArgs[0] === 'checkout' && (gitArgs[1] === '-b' || gitArgs[1] === '-B') && workDir) {
        workspaceBranches.set(workDir, gitArgs[2]!)
        return commandResult()
      }

      if (gitArgs[0] === 'remote' && gitArgs[1] === 'get-url' && gitArgs[2] === 'origin') {
        return commandResult(`${testGitUrl}\n`)
      }

      if (gitArgs[0] === 'rev-parse' && gitArgs.includes('--abbrev-ref') && workDir) {
        return commandResult(
          `${workspaceBranches.get(workDir) ?? workspaceBranches.get(preparingSibling(workDir)) ?? 'main'}\n`,
        )
      }

      return commandResult()
    },
  }
}

function work(workflowRunId: string, issueNumber: number, gitUrl: string) {
  return {
    workflowRunId,
    workId: 'proposal.1',
    workType: 'task',
    uses: 'mohist/opencode',
    variables: {
      workflow: { runId: workflowRunId },
      issue: { number: issueNumber, projectId: 'project-1' },
      repository: {
        name: 'main',
        gitUrl,
        baseBranch: 'main',
      },
    },
  }
}

function removalQuery(projectId: string, workspaceName: string) {
  return {
    projectId,
    workspaceName,
    issueNumber: 1,
    repositoryName: 'main',
    gitUrl: testGitUrl,
    branch: `mohist/ws-${workspaceName}`,
    baseBranch: 'main',
  }
}

function completedRemovalFence(): WorkspaceRemovalFence {
  return {
    async withRemovalFence<T>(_path: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>> {
      return { kind: 'completed', value: await callback() }
    },
  }
}

async function registerNamedWorkspace(
  resources: TestResources,
  runnerRoot: string,
  projectId: string,
  workspaceName: string,
): Promise<{ registry: NamedWorkspaceRegistry; workspacePath: string }> {
  const registry = new NamedWorkspaceRegistry(runnerRoot)
  await registry.load()
  const workspacePath = namedWorkspacePath(runnerRoot, projectId, workspaceName)
  await resources.fileSystem.ensureDir(join(workspacePath, 'REPOS', 'main', '.git'))
  await resources.fileSystem.writeText(
    join(workspacePath, '.mohist', 'workspace.json'),
    JSON.stringify({ projectId, workspaceName, repositories: [{ name: 'main', gitUrl: testGitUrl }] }),
  )
  await registry.register({ projectId, workspaceName, workspacePath })
  return { registry, workspacePath }
}

describe('workspace registry lifecycle', () => {
  const root = '/virtual/workspace-registry'

  it('materialization registers an active entry by workflow run', async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, 'runner')
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work('wr-001', 42, repo), new AbortController().signal)

    const entry = registry.get('wr-001')
    expect(entry).toMatchObject({
      issueNumber: 42,
      workflowRunId: 'wr-001',
      workspacePath: info.path,
      phase: 'active',
    })
    expect(entry?.materializedAt).toBeTruthy()
    expect(entry?.terminalAt).toBeNull()

    // The registry is persisted atomically to disk.
    const persisted = JSON.parse(await resources.fileSystem.readText(defaultWorkspaceRegistryFilePath(runnerRoot)))
    expect(persisted.entries['wr-001']).toMatchObject({ phase: 'active', workflowRunId: 'wr-001' })
  })

  it('materialization writes only workspace identity to the marker', async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, 'runner')
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const info = await manager.prepare(work('wr-marker', 99, repo), new AbortController().signal)

    const marker = JSON.parse(await resources.fileSystem.readText(join(info.path, '.mohist/workspace.json')))
    expect(marker).toMatchObject({
      workflowRunId: 'wr-marker',
      runBranch: 'mohist/run-wr-marker',
    })
  })

  it('verification refreshes an existing entry', async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, 'runner')
    const first = new Date('2026-06-01T00:00:00.000Z')
    const second = new Date('2026-06-25T12:00:00.000Z')
    const now = vi.fn<() => Date>().mockReturnValueOnce(first).mockReturnValueOnce(second)
    const registry = new WorkspaceRegistry(runnerRoot, { now })
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const item = work('wr-refresh', 1, repo)
    await manager.prepare(item, new AbortController().signal)
    expect(registry.get('wr-refresh')?.materializedAt).toBe(first.toISOString())

    await manager.verify(item, new AbortController().signal)
    expect(registry.get('wr-refresh')?.materializedAt).toBe(second.toISOString())
    expect(registry.get('wr-refresh')?.phase).toBe('active')
  })

  it('verification does not create a missing registry entry', async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, 'runner')
    const registry = new WorkspaceRegistry(runnerRoot)
    await registry.load()
    const manager = new WorkspaceManager(runnerRoot, registry)

    const item = work('wr-existing-only', 1, repo)
    await manager.prepare(item, new AbortController().signal)

    // Drop the registry entry directly (simulate a stale / pre-registry
    // disk state) but leave the marker intact.
    await registry.remove('wr-existing-only')

    const verified = await manager.verify(item, new AbortController().signal)
    expect(verified.path).toBeTruthy()
    expect(registry.get('wr-existing-only')).toBeNull()
  })

  it('a fresh registry loads an active entry', async (resources) => {
    const repo = testGitUrl
    const runnerRoot = join(root, 'runner')

    // First host: materialize + register, then "die".
    const registryA = new WorkspaceRegistry(runnerRoot)
    await registryA.load()
    const managerA = new WorkspaceManager(runnerRoot, registryA)
    await managerA.prepare(work('wr-persist', 1, repo), new AbortController().signal)

    // Second host: fresh registry instance, simulates restart.
    const registryB = new WorkspaceRegistry(runnerRoot)
    await registryB.load()

    const entry = registryB.get('wr-persist')
    expect(entry).toMatchObject({
      phase: 'active',
      workflowRunId: 'wr-persist',
      issueNumber: 1,
    })
    expect(entry?.terminalAt).toBeNull()
  })

  it('manual removal drops the matching named registry entry', async (resources) => {
    const runnerRoot = join(root, 'runner')
    const { registry } = await registerNamedWorkspace(resources, runnerRoot, 'project-1', 'issue-remove')
    expect(registry.get('project-1', 'issue-remove')).not.toBeNull()

    const removeHandler = createWorkspaceRemovalHandler({ runnerRoot, registry, removalFence: completedRemovalFence })

    const result = await removeHandler(removalQuery('project-1', 'issue-remove'))

    expect(result).toMatchObject({ removed: true, status: 'removed' })
    expect(registry.get('project-1', 'issue-remove')).toBeNull()
  })

  it('manual removal clears the named entry when the workspace is already missing', async (resources) => {
    const runnerRoot = join(root, 'runner')
    const { registry, workspacePath } = await registerNamedWorkspace(resources, runnerRoot, 'project-1', 'issue-gone')
    await resources.fileSystem.deleteDirectory(workspacePath)
    expect(resources.fileSystem.exists(workspacePath)).toBe(false)

    const removeHandler = createWorkspaceRemovalHandler({ runnerRoot, registry, removalFence: completedRemovalFence })
    const result = await removeHandler(removalQuery('project-1', 'issue-gone'))

    expect(result).toMatchObject({ removed: false, status: 'missing' })
    expect(registry.get('project-1', 'issue-gone')).toBeNull()
  })
})
