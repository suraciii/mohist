import { describe, expect, it } from 'vitest'
import type {
  WorkflowTaskCleanupLease,
  WorkflowTaskExecutionIdentity,
  WorkflowTaskSourceAdoptionRequest,
} from '../src/core/types.js'
import { AdoptTaskSourceChanges, ScopedWorkspaceCleanup } from '../src/runtime/workspace-recovery.js'
import { markerPath } from '../src/runtime/workspace-identity.js'
import { withTestRunnerResources } from './support/test-resources.js'

const root = '/virtual/recovery-runner'
const workspace = `${root}/workspaces/wr-recovery`
const identity: WorkflowTaskExecutionIdentity = {
  workflowRunId: 'wr-recovery',
  stage: 'build',
  taskAttemptId: 'task.1',
  workId: 'work-1',
  ownerKind: 'workflow',
  ownerId: 'wr-recovery',
  runnerId: 'runner-1',
  workspaceId: 'workspace-1',
  workspaceGeneration: 7,
}

function lease(overrides: Partial<WorkflowTaskCleanupLease> = {}): WorkflowTaskCleanupLease {
  return {
    operationId: 'cleanup-1',
    fence: 'fence-1',
    identity,
    boundaryFingerprint: 'boundary-1',
    cleanupScope: ['.mohist/generated.tmp'],
    expiresAt: '2030-01-01T00:00:00.000Z',
    workBudget: 2,
    grantedAt: '2029-01-01T00:00:00.000Z',
    ...overrides,
  }
}

function registry() {
  return {
    get: () => ({
      issueNumber: 1,
      workflowRunId: identity.workflowRunId,
      workspacePath: workspace,
      binding: {
        runnerId: identity.runnerId,
        runnerRoot: root,
        workflowRunId: identity.workflowRunId,
        gitUrl: 'https://repo',
        baseBranch: 'main',
      },
      runBranch: 'mohist/run-wr-recovery',
      workspaceId: identity.workspaceId,
      workspaceGeneration: identity.workspaceGeneration,
      phase: 'active' as const,
      materializedAt: '2029-01-01T00:00:00.000Z',
      terminalAt: null,
    }),
  }
}

async function seed(fileSystem: {
  ensureDir(path: string): Promise<void>
  writeText(path: string, content: string): Promise<void>
}) {
  await fileSystem.ensureDir(`${workspace}/.mohist`)
  await fileSystem.writeText(
    markerPath(workspace),
    JSON.stringify({
      workflowRunId: identity.workflowRunId,
      runBranch: 'mohist/run-wr-recovery',
      workspaceId: identity.workspaceId,
      workspaceGeneration: identity.workspaceGeneration,
    }),
  )
  await fileSystem.writeText(`${workspace}/.mohist/generated.tmp`, 'generated')
  await fileSystem.writeText(`${workspace}/src.ts`, 'source')
}

describe('generation-aware workspace recovery', () => {
  it('deletes only explicitly scoped generated paths and replays without a second mutation', async () => {
    await withTestRunnerResources(async (fileSystem) => {
      await seed(fileSystem)
      const executor = new ScopedWorkspaceCleanup(root, registry(), { now: () => new Date('2029-01-01T00:00:00.000Z') })
      const first = await executor.execute(lease(), workspace)
      const second = await executor.execute(lease(), workspace)

      expect(first.rejected).toBe(false)
      expect(first.operation.removedPaths).toEqual(['.mohist/generated.tmp'])
      expect(fileSystem.exists(`${workspace}/.mohist/generated.tmp`)).toBe(false)
      expect(fileSystem.exists(`${workspace}/src.ts`)).toBe(true)
      expect(second.operation).toEqual(first.operation)
    })
  })

  it('rejects stale fences, unsafe paths, and marker changes before mutation', async () => {
    await withTestRunnerResources(async (fileSystem) => {
      await seed(fileSystem)
      const executor = new ScopedWorkspaceCleanup(root, registry(), { now: () => new Date('2031-01-01T00:00:00.000Z') })
      const expired = await executor.execute(
        lease({ operationId: 'expired', expiresAt: '2030-01-01T00:00:00.000Z' }),
        workspace,
      )
      expect(expired.rejected).toBe(true)
      expect(fileSystem.exists(`${workspace}/.mohist/generated.tmp`)).toBe(true)

      const unsafe = await executor.execute(lease({ operationId: 'unsafe', cleanupScope: ['../outside'] }), workspace)
      expect(unsafe.rejected).toBe(true)
      expect(fileSystem.exists(`${workspace}/.mohist/generated.tmp`)).toBe(true)
    })
  })

  it('adopts only an explicit source allowlist with path-limited git commands', async () => {
    await withTestRunnerResources(async (fileSystem) => {
      await seed(fileSystem)
      const commands: string[][] = []
      const adoption = new AdoptTaskSourceChanges(
        root,
        registry(),
        async (_dir, args) => {
          commands.push(args)
          if (args[0] === 'status')
            return { success: true, exitCode: 0, stdout: ' M src.ts\0', stderr: '', combinedOutput: '' }
          if (args[0] === 'rev-parse')
            return { success: true, exitCode: 0, stdout: 'new-head\n', stderr: '', combinedOutput: 'new-head\n' }
          return { success: true, exitCode: 0, stdout: '', stderr: '', combinedOutput: '' }
        },
        { now: () => new Date('2029-01-01T00:00:00.000Z') },
      )
      const request: WorkflowTaskSourceAdoptionRequest = {
        operationId: 'adopt-1',
        identity,
        boundaryFingerprint: 'boundary-1',
        fence: 'fence-1',
        operatorId: 'operator-1',
        authenticated: true,
        hasWorkflowPermission: true,
        sourcePaths: ['src.ts'],
        protectedPaths: ['.mohist/generated.tmp'],
      }
      const result = await adoption.execute(request, workspace)
      expect(result.rejected).toBe(false)
      expect(result.operation).toMatchObject({ completed: true, resultingHead: 'new-head' })
      expect(commands.some((args) => args[0] === 'add' && args.includes('src.ts'))).toBe(true)
      expect(commands.some((args) => args[0] === 'commit' && args.includes('src.ts'))).toBe(true)
      expect(commands.flat().some((arg) => ['reset', 'clean', 'checkout', 'restore'].includes(arg))).toBe(false)
    })
  })
})
