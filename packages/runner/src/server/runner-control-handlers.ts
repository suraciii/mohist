import type { RunnerControlHandlers } from './runner-control-dispatcher.js'
import { createCancelHandler, type CancelHandlerDeps } from './cancel-handler.js'
import { createFollowupHandler, type FollowupHandlerDeps } from './followup-handler.js'
import { createSessionCommandHandler, type SessionCommandHandlerDeps } from './session-command-handler.js'
import { createSessionProbeHandler, type SessionProbeHandlerDeps } from './session-probe-handler.js'
import { createWorkspaceGitHandlers, type WorkspaceGitHandlerDeps } from './workspace-git-handlers.js'
import { createWorkspaceRemovalHandler, type WorkspaceRemovalHandlerDeps } from './workspace-removal-handler.js'

export interface RunnerControlHandlerDeps {
  workspaceGit: WorkspaceGitHandlerDeps
  workspaceRemoval: WorkspaceRemovalHandlerDeps
  followup: FollowupHandlerDeps
  cancel: CancelHandlerDeps
  sessionCommand: SessionCommandHandlerDeps
  sessionProbe: SessionProbeHandlerDeps
}

export function createRunnerControlHandlers(deps: RunnerControlHandlerDeps): RunnerControlHandlers {
  const git = createWorkspaceGitHandlers(deps.workspaceGit)
  const remove = createWorkspaceRemovalHandler(deps.workspaceRemoval)
  const followup = createFollowupHandler(deps.followup)
  const cancel = createCancelHandler(deps.cancel)
  const command = createSessionCommandHandler(deps.sessionCommand)
  const probe = createSessionProbeHandler(deps.sessionProbe)
  return {
    workspaceDiff: git.getDiff,
    workspaceCommits: git.getCommits,
    workspaceCommitDiff: git.getCommitDiff,
    workspaceStatus: git.getWorkspaceStatus,
    workspaceFileContent: git.getFileContent,
    workspaceRemove: remove,
    sessionFollowup: followup,
    sessionStop: cancel,
    sessionCommand: command,
    sessionProbe: probe,
  }
}
