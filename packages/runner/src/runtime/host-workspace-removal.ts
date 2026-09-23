import { randomUUID } from 'node:crypto'
import type { ServerConnection } from '../server/connection.js'
import { currentRunnerResources } from '../system/filesystem.js'
import { runnerLogger } from '../system/logger.js'
import type { CodexRuntime } from './codex/index.js'
import { createNamedWorkspaceCleanupLoop } from './named-workspace-cleanup.js'
import type { OpenCodeRuntime } from './opencode/index.js'
import type { PiRuntime } from './pi/index.js'
import { RunnerWorkspaceUse } from './runner-workspace-use.js'
import { inspectWorkspaceProcessUse, snapshotRunnerProcessIdentities } from './workspace-process-use.js'
import type { NamedWorkspaceRegistry } from './workspace-registry.js'

const log = runnerLogger.child('host')

export function createHostWorkspaceRemoval(deps: {
  runnerRoot: string
  registry: NamedWorkspaceRegistry
  connection: ServerConnection
  piRuntime: () => PiRuntime | null
  openCodeRuntime: () => OpenCodeRuntime | null
  codexRuntime: () => CodexRuntime | null
}) {
  const workspaceUse = new RunnerWorkspaceUse(
    async (workspacePath) => {
      const piResult = deps.piRuntime()?.releaseWorkspace(workspacePath) ?? 'ready'
      if (piResult !== 'ready') return piResult
      const codexResult = deps.codexRuntime()?.releaseWorkspace(workspacePath) ?? 'ready'
      if (codexResult !== 'ready') return codexResult
      const runtime = deps.openCodeRuntime()
      if (!runtime) return 'ready'
      const result = await runtime.reclaimWhere(
        (_directory, owner, unknownHandle) => owner === workspacePath || unknownHandle,
      )
      return result.failed > 0 ? 'failed' : result.busy > 0 ? 'busy' : 'ready'
    },
    undefined,
    (workspacePath) => {
      const entry = deps.registry.findByWorkspacePath(workspacePath)
      if (entry)
        void deps.registry.markActive(entry.projectId, entry.workspaceName).catch((error) => {
          log.error('failed to reset Workspace candidate period after admission', { exception: error })
        })
    },
    inspectWorkspaceProcessUse,
    currentRunnerResources()?.processSpawner || currentRunnerResources()?.commandRunner
      ? () => new Set()
      : snapshotRunnerProcessIdentities,
  )
  const namedCleanupLoop = createNamedWorkspaceCleanupLoop(
    deps.registry,
    deps.runnerRoot,
    () => workspaceUse,
    async (projectId, workspaceName) => {
      const decision = await deps.connection.getWorkspaceReclaimability(
        projectId,
        workspaceName,
        new AbortController().signal,
      )
      return decision.reclaimable
    },
    async (entry, outcome, reason) => {
      await deps.connection.reportWorkspaceDirectoryObservation(
        entry.projectId,
        entry.workspaceName,
        { attemptId: randomUUID(), homePath: entry.workspacePath, outcome, reason },
        new AbortController().signal,
      )
    },
  )
  return { workspaceUse, namedCleanupLoop }
}
