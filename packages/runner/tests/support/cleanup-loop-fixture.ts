import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import { expect, vi } from "vitest"
import { CleanupLoop, type CleanupRunner } from "../../src/runtime/cleanup-loop.js"
import { WorkspaceRegistry, type WorkspaceRegistryEntry } from "../../src/runtime/workspace-registry.js"
import { capturedLogs } from "./logger-test.js"

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

  async readMarkerWorkflowRunId(workspacePath: string): Promise<string | null | undefined> {
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

export interface CleanupLoopFixture {
  readonly root: string
  readonly now: Date
  readonly registry: WorkspaceRegistry
  readonly runner: StubCleanupRunner
  readonly loop: CleanupLoop
  workspacePath(workItemNumber: number): string
  registerActive(
    workflowRunId: string,
    issueNumber: number,
    workspacePath?: string,
  ): Promise<WorkspaceRegistryEntry>
  registerEligible(
    workflowRunId: string,
    issueNumber: number,
    terminalAt: Date,
    workspacePath?: string,
  ): Promise<string>
  registerEligibleWithoutTerminalAt(
    workflowRunId: string,
    issueNumber: number,
    workspacePath?: string,
  ): Promise<string>
  expectWarnings<T>(messages: readonly string[], operation: () => Promise<T>): Promise<T>
  dispose(): Promise<void>
}

export async function createCleanupLoopFixture(): Promise<CleanupLoopFixture> {
  const root = await mkdtemp(join(tmpdir(), "mohist-cleanup-loop-"))
  const now = new Date("2026-06-25T12:00:00.000Z")
  let registryNow = now
  const runner = new StubCleanupRunner()
  const registry = new WorkspaceRegistry(root, { now: () => registryNow })
  await registry.load()
  const loop = new CleanupLoop(registry, runner, root)

  vi.useFakeTimers({ toFake: ["Date"] })
  vi.setSystemTime(now)

  const workspacePath = (workItemNumber: number) => join(root, "workspaces", `work-item-${workItemNumber}`)
  const registerActive = async (
    workflowRunId: string,
    issueNumber: number,
    path = workspacePath(issueNumber),
  ): Promise<WorkspaceRegistryEntry> => {
    const entry = await registry.register({
      issueNumber,
      workflowRunId,
      workspacePath: path,
    })
    runner.markerRunIds.set(path, workflowRunId)
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
    issueNumber: number,
    path = workspacePath(issueNumber),
  ): Promise<string> => {
    await mkdir(dirname(registry.getFilePath()), { recursive: true })
    await writeFile(
      registry.getFilePath(),
      JSON.stringify({
          version: 2,
        entries: {
          [workflowRunId]: {
            issueNumber,
            workflowRunId,
            workspacePath: path,
            projectId: "project-1",
            repositoryName: "main",
            baseBranch: "main",
            runBranch: `mohist/run-${workflowRunId}`,
            remoteFingerprint: "fingerprint",
            remoteIdentityVersion: "git-remote-url/v1",
            phase: "eligible",
            materializedAt: now.toISOString(),
          },
        },
      }),
    )
    await registry.reload()
    runner.markerRunIds.set(path, workflowRunId)
    return path
  }
  const expectWarnings = async <T>(messages: readonly string[], operation: () => Promise<T>): Promise<T> => {
    const start = capturedLogs().length
    const result = await operation()
    const warnings = capturedLogs().slice(start).filter((record) => record.level === "WARN")
    expect(warnings).toHaveLength(messages.length)
    for (const [index, message] of messages.entries()) {
      const separator = message.includes(" — ") ? " — " : " - "
      const separatorIndex = message.indexOf(separator)
      expect(warnings[index]).toEqual(expect.objectContaining({
        message: "workspace cleanup refused",
        fields: expect.objectContaining({
          path: message.slice("workspace cleanup: refused to remove ".length, separatorIndex),
          reason: message.slice(separatorIndex + separator.length),
        }),
      }))
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
      await rm(root, { recursive: true, force: true })
    },
  }
}
