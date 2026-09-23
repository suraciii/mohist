import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { namedWorkspacePath } from '../src/runtime/workspace-entity.js'
import { namedWorkspaceRegistryKey } from '../src/runtime/workspace-registry.js'
import { WorkExecutor } from '../src/runtime/executor.js'
import type { ServerConnection } from '../src/server/connection.js'
import type { DispatchWorkItem } from '../src/core/types.js'
import { defineTestActions } from './support/action-registry-test.js'
import {
  BASE_BRANCH,
  DIRECTORY_ENTRY_CONTENT,
  FILE_CONTENT,
  GIT_URL,
  PENDING_ARTIFACT_ID,
  PROJECT_ID,
  REPOSITORY_NAME,
  ROOT,
  RUN_BRANCH,
  STAGE_ONE_WORK_ID,
  STAGE_TWO_WORK_ID,
  WORKFLOW_RUN_ID,
  WORKSPACE_NAME,
  directoryArtifact,
  fileArtifact,
  provisionNamedWorkspace,
  withWorkspaceHomeRecovery,
} from './support/workspace-home-recovery-fixture.js'

// Design Spec for the Runner-owned half of Workspace Home continuity:
// `NamedWorkspaceManager.provisionForIssue` must rebuild a deleted Home from
// the durable inputs (the remote `mohist/ws-<workspaceName>` run branch and
// the Server-bound non-Git artifacts) and must fail closed before the Action
// runs when a served artifact does not match its recorded content hash.

function buildWork(workId: string): DispatchWorkItem {
  return {
    workflowRunId: WORKFLOW_RUN_ID,
    workId,
    workType: 'task',
    projectId: PROJECT_ID,
    uses: 'test/record',
    variables: {
      workspace: { name: WORKSPACE_NAME, branch: RUN_BRANCH },
      repository: { name: REPOSITORY_NAME, gitUrl: GIT_URL, baseBranch: BASE_BRANCH },
    },
  }
}

describe('Workspace Home recovery', () => {
  it('rebuilds a deleted Home from the same run branch and bound artifacts', async () => {
    await withWorkspaceHomeRecovery(async ({ registry, manager, connection, gitCalls }, fileSystem) => {
      const homePath = namedWorkspacePath(ROOT, PROJECT_ID, WORKSPACE_NAME)

      const first = await provisionNamedWorkspace(manager, STAGE_ONE_WORK_ID)
      expect(first.path).toBe(homePath)
      expect(registry.get(PROJECT_ID, WORKSPACE_NAME)).toMatchObject({ workspacePath: homePath, phase: 'active' })
      await expect(fileSystem.lstat(join(homePath, 'REPOS', REPOSITORY_NAME))).resolves.toBeDefined()
      await expect(fileSystem.readText(join(homePath, 'PLANS', 'tasks.json'))).resolves.toBe(FILE_CONTENT)
      await expect(fileSystem.readText(join(homePath, 'RESEARCH', 'notes.txt'))).resolves.toBe(DIRECTORY_ENTRY_CONTENT)
      expect(connection.reportCalls).toEqual([{ projectId: PROJECT_ID, workspaceName: WORKSPACE_NAME, path: homePath }])

      // A valid Home is the source of truth. Later tasks keep local edits and
      // do not re-read the older artifact snapshot.
      await fileSystem.writeText(join(homePath, 'PLANS', 'tasks.json'), 'local edit')
      const existing = await provisionNamedWorkspace(manager, STAGE_TWO_WORK_ID)
      expect(existing).toMatchObject({ path: homePath, created: false })
      await expect(fileSystem.readText(join(homePath, 'PLANS', 'tasks.json'))).resolves.toBe('local edit')
      expect(connection.listRequests).toHaveLength(1)
      expect(connection.downloadRequests).toHaveLength(2)

      // Home loss: both the directory and its registry entry are gone.
      await fileSystem.deleteDirectory(homePath)
      await registry.remove(namedWorkspaceRegistryKey(PROJECT_ID, WORKSPACE_NAME))
      expect(fileSystem.exists(homePath)).toBe(false)
      expect(registry.get(PROJECT_ID, WORKSPACE_NAME)).toBeNull()

      const second = await provisionNamedWorkspace(manager, STAGE_TWO_WORK_ID)
      expect(second.path).toBe(homePath)
      expect(registry.get(PROJECT_ID, WORKSPACE_NAME)).toMatchObject({ workspacePath: homePath, phase: 'active' })
      await expect(fileSystem.readText(join(homePath, 'PLANS', 'tasks.json'))).resolves.toBe(FILE_CONTENT)
      await expect(fileSystem.readText(join(homePath, 'RESEARCH', 'notes.txt'))).resolves.toBe(DIRECTORY_ENTRY_CONTENT)

      const cloneCalls = gitCalls.filter((call) => call.args[0] === 'clone')
      expect(cloneCalls).toHaveLength(2)
      for (const call of cloneCalls) {
        expect(call.args).toContain(GIT_URL)
        expect(call.args.at(-1)!.startsWith(homePath)).toBe(true)
      }

      const runBranchCheckouts = gitCalls.filter((call) => call.args[2] === 'checkout' && call.args[3] === '-B')
      expect(runBranchCheckouts).toHaveLength(2)
      for (const call of runBranchCheckouts) {
        expect(call.args[4]).toBe(RUN_BRANCH)
        expect(call.args[5]).toBe(`origin/${RUN_BRANCH}`)
        expect(call.args).not.toContain(`origin/${BASE_BRANCH}`)
      }

      expect(connection.listRequests).toEqual([
        { workflowRunId: WORKFLOW_RUN_ID, workId: STAGE_ONE_WORK_ID },
        { workflowRunId: WORKFLOW_RUN_ID, workId: STAGE_TWO_WORK_ID },
      ])
      const downloadKeys = connection.downloadRequests.map((request) => `${request.artifactId}:${request.file ?? ''}`)
      expect(downloadKeys).toEqual(['art_plan:', 'art_research:notes.txt', 'art_plan:', 'art_research:notes.txt'])
      const requestedIds = new Set(connection.downloadRequests.map((request) => request.artifactId))
      expect(requestedIds).toEqual(new Set([fileArtifact.artifactId, directoryArtifact.artifactId]))
      expect(requestedIds.has(PENDING_ARTIFACT_ID)).toBe(false)
    })
  })

  it('rejects mismatched artifact bytes and leaves no committed Home or active registry entry', async () => {
    await withWorkspaceHomeRecovery(
      async ({ registry, manager, connection }, fileSystem) => {
        const homePath = namedWorkspacePath(ROOT, PROJECT_ID, WORKSPACE_NAME)

        await expect(provisionNamedWorkspace(manager, STAGE_TWO_WORK_ID)).rejects.toThrow(/content hash mismatch/)

        expect(fileSystem.exists(homePath)).toBe(false)
        expect(registry.get(PROJECT_ID, WORKSPACE_NAME)).toBeNull()
        expect(connection.reportCalls).toEqual([])
        expect(connection.downloadRequests.map((request) => request.artifactId)).toEqual([fileArtifact.artifactId])
      },
      { tamperFileBytes: true },
    )
  })

  it('stops before invoking the Action when workspace provisioning fails closed', async () => {
    await withWorkspaceHomeRecovery(
      async ({ registry, manager, connection }, fileSystem) => {
        const homePath = namedWorkspacePath(ROOT, PROJECT_ID, WORKSPACE_NAME)
        let invoked = 0
        const actions = defineTestActions({
          'test/record': async () => {
            invoked += 1
            return { output: { invoked: true } }
          },
        })
        const executor = new WorkExecutor(actions, manager, connection as unknown as ServerConnection, ROOT)

        const result = await executor.execute(buildWork(STAGE_TWO_WORK_ID), new AbortController().signal)

        expect(result.status).toBe('failed')
        expect(result.error?.code).toBe('workspace-setup')
        expect(invoked).toBe(0)
        expect(connection.reportCalls).toEqual([])
        expect(fileSystem.exists(homePath)).toBe(false)
        expect(registry.get(PROJECT_ID, WORKSPACE_NAME)).toBeNull()
      },
      { tamperFileBytes: true },
    )
  })
})
