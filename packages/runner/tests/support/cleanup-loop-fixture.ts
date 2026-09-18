import { dirname, join } from 'node:path'
import { AsyncLocalStorage } from 'node:async_hooks'
import { expect, vi } from 'vitest'
import { CleanupLoop, type CleanupRegistry, type CleanupRunner } from '../../src/runtime/cleanup-loop.js'
import type { RunnerFileSystem } from '../../src/system/filesystem.js'
import {
  NamedWorkspaceRegistry,
  namedWorkspaceRegistryKey,
  type NamedWorkspaceRegistryEntry,
} from '../../src/runtime/workspace-registry.js'
import { capturedLogs } from './logger-test.js'
import { withTestRunnerResources } from './test-resources.js'

const PROJECT_ID = 'project-1'

export class StubCleanupRunner implements CleanupRunner {
  public deletedPaths: string[] = []
  public failedDeletePaths = new Set<string>()
  public markerRunIds = new Map<string, string | null | undefined>()
  public outOfRootPaths = new Set<string>()
  public sizes = new Map<string, number>()
  public missingPaths = new Set<string>()

  isUnderRunnerRoot(_root: string, candidate: string): boolean {
    return !this.outOfRootPaths.has(candidate)
  }

  pathExists(path: string): boolean {
    return !this.missingPaths.has(path)
  }

  async readWorkspaceIdentity(workspacePath: string): Promise<string | null | undefined> {
    return this.markerRunIds.get(workspacePath)
  }

  async deleteDirectory(path: string): Promise<void> {
    if (this.failedDeletePaths.has(path)) throw new Error(`stub delete failed: ${path}`)
    this.deletedPaths.push(path)
  }

  async computeDirectorySize(path: string, _signal: AbortSignal): Promise<number | null> {
    if (this.sizes.has(path)) return this.sizes.get(path) ?? null
    return 200_000
  }
}

// Thin adapter over the real NamedWorkspaceRegistry so the cleanup-loop
// specs keep addressing entries by workspace name. Identity keys still come
// from the registry, so the marker comparison the loop performs is real.
class FixtureRegistry implements CleanupRegistry<NamedWorkspaceRegistryEntry> {
  constructor(private readonly registry: NamedWorkspaceRegistry) {}

  getFilePath(): string {
    return this.registry.getFilePath()
  }

  get(workspaceName: string): NamedWorkspaceRegistryEntry | null {
    return this.registry.get(PROJECT_ID, workspaceName)
  }

  list(): NamedWorkspaceRegistryEntry[] {
    return this.registry.list()
  }

  entryKey(entry: NamedWorkspaceRegistryEntry): string {
    return this.registry.entryKey(entry)
  }

  markStuck(key: string): Promise<NamedWorkspaceRegistryEntry | null> {
    return this.registry.markStuck(key)
  }

  remove(key: string): Promise<boolean> {
    return this.registry.remove(key)
  }

  reload(): Promise<void> {
    return this.registry.reload()
  }

  registerActive(workspaceName: string, workspacePath: string): Promise<NamedWorkspaceRegistryEntry> {
    return this.registry.register({ projectId: PROJECT_ID, workspaceName, workspacePath })
  }

  markEligible(workspaceName: string): Promise<NamedWorkspaceRegistryEntry | null> {
    return this.registry.markEligible(PROJECT_ID, workspaceName)
  }
}

export interface CleanupLoopFixture {
  readonly root: string
  readonly now: Date
  readonly registry: FixtureRegistry
  readonly runner: StubCleanupRunner
  readonly loop: CleanupLoop<NamedWorkspaceRegistryEntry>
  workspacePath(workItemNumber: number): string
  registerActive(
    workflowRunId: string,
    issueNumber: number,
    workspacePath?: string,
  ): Promise<NamedWorkspaceRegistryEntry>
  registerEligible(
    workflowRunId: string,
    issueNumber: number,
    terminalAt: Date,
    workspacePath?: string,
  ): Promise<string>
  registerEligibleWithoutTerminalAt(workflowRunId: string, issueNumber: number, workspacePath?: string): Promise<string>
  expectWarnings<T>(messages: readonly string[], operation: () => Promise<T>): Promise<T>
  dispose(): Promise<void>
}

export async function createCleanupLoopFixture(fileSystem: RunnerFileSystem): Promise<CleanupLoopFixture> {
  const root = '/virtual/cleanup-loop'
  const now = new Date('2026-06-25T12:00:00.000Z')
  let registryNow = now
  const runner = new StubCleanupRunner()
  const backing = new NamedWorkspaceRegistry(root, { now: () => registryNow })
  await backing.load()
  const registry = new FixtureRegistry(backing)
  const loop = new CleanupLoop<NamedWorkspaceRegistryEntry>(registry, runner, root)

  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(now)

  const workspacePath = (workItemNumber: number) => join(root, 'workspaces', `work-item-${workItemNumber}`)
  const registerActive = async (
    workflowRunId: string,
    _issueNumber: number,
    path = workspacePath(1),
  ): Promise<NamedWorkspaceRegistryEntry> => {
    const entry = await registry.registerActive(workflowRunId, path)
    runner.markerRunIds.set(path, namedWorkspaceRegistryKey(PROJECT_ID, workflowRunId))
    return entry
  }
  const registerEligible = async (
    workflowRunId: string,
    issueNumber: number,
    terminalAt: Date,
    path = workspacePath(issueNumber),
  ): Promise<string> => {
    await registerActive(workflowRunId, issueNumber, path)
    const previousRegistryNow = registryNow
    registryNow = terminalAt
    try {
      await registry.markEligible(workflowRunId)
    } finally {
      registryNow = previousRegistryNow
    }
    return path
  }
  const registerEligibleWithoutTerminalAt = async (
    workflowRunId: string,
    _issueNumber: number,
    path = workspacePath(1),
  ): Promise<string> => {
    await fileSystem.ensureDir(dirname(registry.getFilePath()))
    await fileSystem.writeText(
      registry.getFilePath(),
      JSON.stringify({
        version: 1,
        entries: {
          [namedWorkspaceRegistryKey(PROJECT_ID, workflowRunId)]: {
            projectId: PROJECT_ID,
            workspaceName: workflowRunId,
            workspacePath: path,
            phase: 'eligible',
            provisionedAt: now.toISOString(),
            terminalAt: null,
          },
        },
      }),
    )
    await registry.reload()
    runner.markerRunIds.set(path, namedWorkspaceRegistryKey(PROJECT_ID, workflowRunId))
    return path
  }
  const expectWarnings = async <T>(messages: readonly string[], operation: () => Promise<T>): Promise<T> => {
    const start = capturedLogs().length
    const result = await operation()
    const warnings = capturedLogs()
      .slice(start)
      .filter((record) => record.level === 'WARN')
    expect(warnings).toHaveLength(messages.length)
    for (const [index, message] of messages.entries()) {
      const separator = message.includes(' — ') ? ' — ' : ' - '
      const separatorIndex = message.indexOf(separator)
      expect(warnings[index]).toEqual(
        expect.objectContaining({
          message: 'workspace cleanup refused',
          fields: expect.objectContaining({
            path: message.slice('workspace cleanup: refused to remove '.length, separatorIndex),
            reason: message.slice(separatorIndex + separator.length),
          }),
        }),
      )
    }
    return result
  }

  return {
    root,
    now,
    registry,
    runner,
    loop,
    workspacePath,
    registerActive,
    registerEligible,
    registerEligibleWithoutTerminalAt,
    expectWarnings,
    async dispose() {
      vi.useRealTimers()
      await fileSystem.deleteDirectory(root)
      if (fileSystem.exists(root)) throw new Error(`cleanup loop fixture root was not cleaned: ${root}`)
    },
  }
}

const cleanupLoopFixtureStorage = new AsyncLocalStorage<CleanupLoopFixture>()

export function currentCleanupLoopFixture(): CleanupLoopFixture {
  const fixture = cleanupLoopFixtureStorage.getStore()
  if (!fixture) throw new Error('cleanup loop fixture context is not active')
  return fixture
}

export function scopedCleanupLoopFixture(): CleanupLoopFixture {
  return new Proxy({} as CleanupLoopFixture, {
    get(_target, property) {
      return Reflect.get(currentCleanupLoopFixture(), property)
    },
  })
}

export async function withCleanupLoopFixture<T>(body: () => Promise<T>): Promise<T> {
  return await withTestRunnerResources(async (fileSystem) => {
    const fixture = await createCleanupLoopFixture(fileSystem)
    return await cleanupLoopFixtureStorage.run(fixture, async () => {
      try {
        return await body()
      } finally {
        await fixture.dispose()
      }
    })
  })
}
