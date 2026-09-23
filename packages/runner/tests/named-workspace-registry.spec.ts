import { join } from 'node:path'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import {
  NamedWorkspaceRegistry,
  defaultNamedWorkspaceRegistryFilePath,
  namedWorkspaceRegistryKey,
} from '../src/runtime/workspace-registry.js'
import { NamedWorkspaceManager, namedWorkspacePath } from '../src/runtime/workspace-entity.js'
import { createTestTempDir } from './support/temp-dir.js'
import { mkdir, readFile, writeFile } from './support/test-fs.js'
import { withTestRunnerResources } from './support/test-resources.js'

const now = new Date('2026-07-01T08:00:00.000Z')

function it(name: string, body: (root: string) => Promise<void>): void {
  vitestIt(name, async () => {
    await withTestRunnerResources(async () => {
      const root = await createTestTempDir('mohist-named-registry-')
      await body(root)
    })
  })
}

describe('NamedWorkspaceRegistry restart safety', () => {
  it('defaults to <runnerRoot>/.mohist/named-workspaces.json', async (root) => {
    expect(defaultNamedWorkspaceRegistryFilePath(root)).toBe(join(root, '.mohist', 'named-workspaces.json'))
  })

  it('starts empty when the named registry file is missing', async (root) => {
    const registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()

    expect(registry.list()).toEqual([])
    expect(registry.get('project', 'pay')).toBeNull()
  })

  it('starts empty when the named registry JSON is corrupt', async (root) => {
    await mkdir(join(root, '.mohist'), { recursive: true })
    await writeFile(defaultNamedWorkspaceRegistryFilePath(root), '{ this is not json')

    const registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()

    expect(registry.list()).toEqual([])
  })

  it('ignores a legacy WorkflowRun registry file', async (root) => {
    await mkdir(join(root, '.mohist'), { recursive: true })
    await writeFile(
      defaultNamedWorkspaceRegistryFilePath(root),
      JSON.stringify({
        version: 3,
        entries: {
          'wr-legacy': {
            issueNumber: 1,
            workflowRunId: 'wr-legacy',
            workspacePath: join(root, 'workspaces', 'wr-legacy'),
            phase: 'active',
            materializedAt: now.toISOString(),
            terminalAt: null,
          },
        },
      }),
    )

    const registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()

    expect(registry.list()).toEqual([])
    expect(registry.findByWorkspacePath(join(root, 'workspaces', 'wr-legacy'))).toBeNull()
  })

  it('does not adopt a run-*/wr-* directory present on disk', async (root) => {
    await mkdir(join(root, 'workspaces', 'wr-leftover'), { recursive: true })
    await mkdir(join(root, 'workspaces', 'run-leftover'), { recursive: true })

    const registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()

    expect(registry.list()).toEqual([])
    expect(registry.findByWorkspacePath(join(root, 'workspaces', 'wr-leftover'))).toBeNull()
    expect(registry.findByWorkspacePath(join(root, 'workspaces', 'run-leftover'))).toBeNull()
  })

  it('loads one valid entry and reloads it after a persist', async (root) => {
    const registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()
    await registry.register({
      projectId: 'project',
      workspaceName: 'pay',
      workspacePath: namedWorkspacePath(root, 'project', 'pay'),
    })

    const reloaded = new NamedWorkspaceRegistry(root, { now: () => now })
    await reloaded.load()
    expect(reloaded.get('project', 'pay')).toMatchObject({
      projectId: 'project',
      workspaceName: 'pay',
      phase: 'active',
    })
    const raw = JSON.parse((await readFile(defaultNamedWorkspaceRegistryFilePath(root), 'utf8')) as string)
    expect(Object.keys(raw.entries)).toEqual([namedWorkspaceRegistryKey('project', 'pay')])
  })

  it('persists the latest phase when candidate status changes during another write', async (root) => {
    const registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()
    const registering = registry.register({
      projectId: 'project',
      workspaceName: 'pay',
      workspacePath: namedWorkspacePath(root, 'project', 'pay'),
    })
    const eligible = registry.markEligible('project', 'pay')
    const active = registry.markActive('project', 'pay')
    await Promise.all([registering, eligible, active])

    const reloaded = new NamedWorkspaceRegistry(root, { now: () => now })
    await reloaded.load()
    expect(reloaded.get('project', 'pay')).toMatchObject({ phase: 'active', terminalAt: null })
  })

  it('never reports a Home for a leftover run directory when materializing a named workspace', async (root) => {
    await mkdir(join(root, 'workspaces', 'wr-leftover'), { recursive: true })
    const registry = new NamedWorkspaceRegistry(root, { now: () => now })
    await registry.load()
    const report = vi.fn(async () => ({ runnerId: 'runner-1', path: 'ignored' }))
    const manager = new NamedWorkspaceManager(root, registry, {
      reportWorkspaceMaterialized: report,
    } as never)

    const result = await manager.provision('project', 'pay', [], new AbortController().signal)

    expect(result.path).toBe(namedWorkspacePath(root, 'project', 'pay'))
    expect(result.path).not.toContain('wr-leftover')
    expect(report).toHaveBeenCalledWith('project', 'pay', result.path, expect.any(AbortSignal), true)
  })
})
