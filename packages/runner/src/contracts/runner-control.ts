import type { AgentExecutionDefinition } from '../core/types.js'
import type { SlackExecutionContext } from '../runtime/slack-execution-context.js'
import type { WorkspaceQuery } from '../runtime/workspace-query.js'
import type { SessionCommandRequest, SessionCommandResult } from '../server/session-command-handler.js'
import type { RuntimeSessionBinding } from '../server/session-target.js'

export interface JsonRpcRequest<TParams> {
  jsonrpc: '2.0'
  id: string
  method: string
  params: TParams
}

export interface JsonRpcNotification<TParams> {
  jsonrpc: '2.0'
  method: string
  params: TParams
}

export interface JsonRpcSuccessResponse<TResult> {
  jsonrpc: '2.0'
  id: string
  result: TResult
}

export interface JsonRpcErrorResponse {
  jsonrpc: '2.0'
  id: string | null
  error: JsonRpcError
}

export interface JsonRpcError {
  code: number
  message: string
  data?: unknown
}

export interface WorkspaceQueryParams {
  query: WorkspaceQuery
}

export interface WorkspaceCommitDiffParams extends WorkspaceQueryParams {
  hash: string
}

export interface WorkspaceFileContentParams extends WorkspaceQueryParams {
  path: string
}

export type RunnerSessionTarget =
  | {
      kind: 'workflow'
      projectId: string
      workflowRunId: string
      sessionName: string
      sessionId?: string
      binding: RuntimeSessionBinding
    }
  | {
      kind: 'generic'
      projectId: string
      sessionId: string
      definition?: AgentExecutionDefinition
      binding: RuntimeSessionBinding
    }

export interface FollowupAttachmentDescriptor {
  id: string
  name: string
  contentType: string | null
  size: number
}

export interface FollowupParams {
  target: RunnerSessionTarget
  text: string
  operationId: string
  inputId?: string | null
  turnId: string
  slackExecutionContext?: SlackExecutionContext | null
  attachments?: readonly FollowupAttachmentDescriptor[] | null
}

export interface SessionStopParams {
  target: RunnerSessionTarget
  sessionId: string
  turnId: string
  operationId: string
}

export interface DiffFile {
  file: string
  additions: number
  deletions: number
  diff: string
  isBinary: boolean
}

export interface GitCommit {
  hash: string
  shortHash: string
  message: string
  author: string
  date: string
  files: readonly string[]
}

export interface RunnerWorkspaceDiffResult {
  base: string
  head: string
  mergeBase: string
  ahead: number
  behind: number
  commitCount: number
  totalAdditions: number
  totalDeletions: number
  files: readonly DiffFile[]
}

export interface RunnerWorkspaceCommitsResult {
  base: string
  head: string
  mergeBase: string
  ahead: number
  behind: number
  filesChanged: number
  totalAdditions: number
  totalDeletions: number
  commits: readonly GitCommit[]
}

export interface RunnerWorkspaceCommitDiffResult {
  diff: string
}

export interface WorkspaceStatus {
  exists: boolean
  reason?: string | null
  branch?: string | null
  baseBranch?: string | null
  ahead?: number
  behind?: number
  rebaseInProgress?: boolean
  conflictingFiles?: readonly string[]
}

export interface RunnerWorkspaceFileContentResult {
  base: string | null
  head: string | null
  reason?: string | null
}

export interface WorkspaceRemovalResult {
  removed: boolean
  status: string
  path: string | null
  reason: string | null
  message: string
}

export interface RunnerFollowupDeliveryResult {
  accepted: boolean
  error?: string | null
}

export interface RunnerStopReply {
  state: string | null
  interruptUnconfirmed?: boolean | null
}

export interface WorkflowRunStatusNotification {
  workflowRunId: string
  status: string
}

export type { SessionCommandRequest, SessionCommandResult }
