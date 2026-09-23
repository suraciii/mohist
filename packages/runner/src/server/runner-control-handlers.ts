import type { RunnerControlHandlers } from './runner-control-dispatcher.js'
import { createCancelHandler, type CancelHandlerDeps } from './cancel-handler.js'
import { createFollowupHandler, type FollowupHandlerDeps } from './followup-handler.js'
import { createSessionCommandHandler, type SessionCommandHandlerDeps } from './session-command-handler.js'
import { createWorkspaceGitHandlers, type WorkspaceGitHandlerDeps } from './workspace-git-handlers.js'
import {
  createWorkspaceInspectionHandler,
  createWorkspaceRemovalHandler,
  type WorkspaceRemovalHandlerDeps,
} from './workspace-removal-handler.js'
import { WorkspaceUseConflict } from '../runtime/runner-workspace-use.js'

export interface RunnerControlHandlerDeps {
  workspaceGit: WorkspaceGitHandlerDeps
  workspaceRemoval: WorkspaceRemovalHandlerDeps
  followup: FollowupHandlerDeps
  cancel: CancelHandlerDeps
  sessionCommand: SessionCommandHandlerDeps
  withWorkspaceUse?: <T>(workDir: string, work: () => Promise<T>) => Promise<T>
}

export function createRunnerControlHandlers(deps: RunnerControlHandlerDeps): RunnerControlHandlers {
  const git = createWorkspaceGitHandlers(deps.workspaceGit)
  const remove = createWorkspaceRemovalHandler(deps.workspaceRemoval)
  const inspect = createWorkspaceInspectionHandler(deps.workspaceRemoval)
  const followup = createFollowupHandler(deps.followup)
  const cancel = createCancelHandler(deps.cancel)
  const command = createSessionCommandHandler(deps.sessionCommand)
  const guarded = async <T>(workDir: string | null | undefined, work: () => Promise<T>): Promise<T> =>
    workDir && deps.withWorkspaceUse ? await deps.withWorkspaceUse(workDir, work) : await work()
  return {
    workspaceDiff: git.getDiff,
    workspaceCommits: git.getCommits,
    workspaceCommitDiff: git.getCommitDiff,
    workspaceStatus: git.getWorkspaceStatus,
    workspaceFileContent: git.getFileContent,
    workspaceRemove: remove,
    workspaceInspect: inspect,
    sessionFollowup: async (payload) => {
      try {
        return await guarded(payload.target?.binding?.workDir, () => followup(payload))
      } catch (error) {
        if (error instanceof WorkspaceUseConflict) return { accepted: false, error: 'workspace-removal-in-progress' }
        throw error
      }
    },
    sessionStop: async (payload) => {
      try {
        return await guarded(payload.target?.binding?.workDir, () => cancel(payload))
      } catch (error) {
        if (error instanceof WorkspaceUseConflict)
          return { state: 'unavailable', error: 'workspace-removal-in-progress' }
        throw error
      }
    },
    sessionCommand: async (request) => {
      try {
        return await guarded(request.workDir, () => command(request))
      } catch (error) {
        if (error instanceof WorkspaceUseConflict) return { ok: false, error: 'conflict' }
        throw error
      }
    },
  }
}
