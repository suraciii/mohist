import type { WorkspaceQuery } from '../runtime/workspace-query.js'
import type {
  CancelAgentSessionPayload,
  ReceiveFollowupPayload,
  ReceiveWorkflowRunStatusPayload,
} from './session-target.js'
import type { SessionCommandRequest } from './session-command-handler.js'
import { readSlackExecutionContext } from '../runtime/slack-execution-context.js'

export const JSON_RPC_PARSE_ERROR = -32700
export const JSON_RPC_INVALID_REQUEST = -32600
export const JSON_RPC_METHOD_NOT_FOUND = -32601
export const JSON_RPC_INVALID_PARAMS = -32602
export const JSON_RPC_INTERNAL_ERROR = -32603

export interface RunnerControlHandlers {
  workspaceDiff(query: WorkspaceQuery): Promise<unknown>
  workspaceCommits(query: WorkspaceQuery): Promise<unknown>
  workspaceCommitDiff(query: WorkspaceQuery, hash: string): Promise<unknown>
  workspaceStatus(query: WorkspaceQuery): Promise<unknown>
  workspaceFileContent(query: WorkspaceQuery, path: string): Promise<unknown>
  workspaceRemove(query: WorkspaceQuery): Promise<unknown>
  sessionFollowup(params: ReceiveFollowupPayload): Promise<unknown>
  sessionStop(params: CancelAgentSessionPayload): Promise<unknown>
  sessionCommand(params: SessionCommandRequest): Promise<unknown>
  workflowStatusChanged(params: ReceiveWorkflowRunStatusPayload): Promise<void> | void
}

export interface RunnerControlDispatcherOutput {
  enqueue(value: unknown, complete?: () => void): boolean
  protocolError(): void
  isCurrent(): boolean
}

type ObjectValue = Record<string, unknown>

export class RunnerControlDispatcher {
  private readonly live = new Set<string>()

  constructor(
    private readonly handlers: RunnerControlHandlers,
    private readonly output: RunnerControlDispatcherOutput,
  ) {}

  receive(text: string): void {
    let value: unknown
    try {
      value = JSON.parse(text)
    } catch {
      this.fail(null, JSON_RPC_PARSE_ERROR, 'Parse error')
      return
    }
    if (!isObject(value)) {
      this.fail(null, JSON_RPC_INVALID_REQUEST, 'Invalid Request')
      return
    }
    const id = nonempty(value.id) ? value.id : null
    const notification = value.id === undefined
    if (
      value.jsonrpc !== '2.0' ||
      !nonempty(value.method) ||
      (!notification && id === null) ||
      !isObject(value.params)
    ) {
      if (notification) this.output.protocolError()
      else this.fail(id, JSON_RPC_INVALID_REQUEST, 'Invalid Request')
      return
    }
    if (notification) {
      this.dispatchNotification(value.method, value.params)
      return
    }
    if (this.live.has(id!)) {
      this.fail(id, JSON_RPC_INVALID_REQUEST, 'Invalid Request')
      return
    }
    this.live.add(id!)
    void this.dispatchRequest(id!, value.method, value.params)
  }

  private async dispatchRequest(id: string, method: string, params: ObjectValue): Promise<void> {
    const complete = () => this.live.delete(id)
    try {
      const call = this.requestCall(method, params)
      if (call === 'unknown') {
        this.fail(id, JSON_RPC_METHOD_NOT_FOUND, 'Method not found', complete)
        return
      }
      if (call === 'invalid') {
        this.fail(id, JSON_RPC_INVALID_PARAMS, 'Invalid params', complete)
        return
      }
      const result = await call()
      if (result === undefined) throw new Error('Runner control handler returned undefined')
      if (this.output.isCurrent() && this.output.enqueue({ jsonrpc: '2.0', id, result }, complete)) return
    } catch {
      if (
        this.output.isCurrent() &&
        this.output.enqueue(errorResponse(id, JSON_RPC_INTERNAL_ERROR, 'Internal error'), complete)
      )
        return
    }
    complete()
  }

  private dispatchNotification(method: string, params: ObjectValue): void {
    if (method !== 'workflow.status-changed') return
    if (!isWorkflowStatus(params)) {
      this.output.protocolError()
      return
    }
    try {
      void Promise.resolve(this.handlers.workflowStatusChanged(params)).catch(() => undefined)
    } catch {
      // Notifications never produce a response, including callback failures.
    }
  }

  private requestCall(method: string, params: ObjectValue): (() => Promise<unknown>) | 'unknown' | 'invalid' {
    switch (method) {
      case 'workspace.diff':
      case 'workspace.commits':
      case 'workspace.status':
      case 'workspace.remove': {
        if (!isWorkspaceWrapper(params)) return 'invalid'
        const query = params.query
        if (method === 'workspace.diff') return () => this.handlers.workspaceDiff(query)
        if (method === 'workspace.commits') return () => this.handlers.workspaceCommits(query)
        if (method === 'workspace.status') return () => this.handlers.workspaceStatus(query)
        return () => this.handlers.workspaceRemove(query)
      }
      case 'workspace.commit-diff':
        return isWorkspaceWrapper(params) && nonempty(params.hash)
          ? () => this.handlers.workspaceCommitDiff(params.query, params.hash as string)
          : 'invalid'
      case 'workspace.file-content':
        return isWorkspaceWrapper(params) && nonempty(params.path)
          ? () => this.handlers.workspaceFileContent(params.query, params.path as string)
          : 'invalid'
      case 'session.followup':
        return isFollowup(params) ? () => this.handlers.sessionFollowup(normalizeFollowup(params)) : 'invalid'
      case 'session.stop':
        return isStop(params) ? () => this.handlers.sessionStop(normalizeStop(params)) : 'invalid'
      case 'session.command':
        return isSessionCommand(params)
          ? () => this.handlers.sessionCommand(normalizeSessionCommand(params))
          : 'invalid'
      default:
        return 'unknown'
    }
  }

  private fail(id: string | null, code: number, message: string, complete?: () => void): void {
    if (!this.output.enqueue(errorResponse(id, code, message), complete)) complete?.()
    this.output.protocolError()
  }
}

function errorResponse(id: string | null, code: number, message: string): unknown {
  return { jsonrpc: '2.0', id, error: { code, message } }
}

function isObject(value: unknown): value is ObjectValue {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function nonempty(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}

function nullableString(value: unknown): boolean {
  return value === undefined || value === null || typeof value === 'string'
}

function isWorkspaceQuery(value: unknown): value is WorkspaceQuery {
  if (!isObject(value)) return false
  return (
    nullableString(value.workflowRunId) &&
    nullableString(value.projectId) &&
    (value.issueNumber === undefined || value.issueNumber === null || Number.isSafeInteger(value.issueNumber)) &&
    nullableString(value.repositoryName) &&
    nullableString(value.gitUrl) &&
    nullableString(value.workspacePath) &&
    nullableString(value.branch) &&
    nullableString(value.baseBranch)
  )
}

function isWorkspaceWrapper(value: ObjectValue): value is ObjectValue & { query: WorkspaceQuery } {
  return isWorkspaceQuery(value.query)
}

function isBinding(value: unknown): boolean {
  return (
    isObject(value) &&
    nonempty(value.runtime) &&
    nonempty(value.runtimeSessionId) &&
    nonempty(value.runnerId) &&
    nullableNonemptyString(value.workDir)
  )
}

function isDefinition(value: unknown): boolean {
  if (value === undefined || value === null) return true
  if (!isObject(value)) return false
  return (
    nullableString(value.instructions) &&
    nullableString(value.runtime) &&
    nullableString(value.model) &&
    nullableString(value.variant) &&
    nullableString(value.reasoningEffort) &&
    (value.skills === undefined ||
      value.skills === null ||
      (Array.isArray(value.skills) && value.skills.every(nonempty)))
  )
}

function isTarget(value: unknown): boolean {
  if (!isObject(value) || !nonempty(value.projectId) || !isBinding(value.binding)) return false
  if (value.kind === 'workflow') {
    return nonempty(value.workflowRunId) && nonempty(value.sessionName) && nullableString(value.sessionId)
  }
  return value.kind === 'generic' && nonempty(value.sessionId) && isDefinition(value.definition)
}

function isAttachment(value: unknown): boolean {
  return (
    isObject(value) &&
    nonempty(value.id) &&
    nonempty(value.name) &&
    nullableNonemptyString(value.contentType) &&
    Number.isSafeInteger(value.size) &&
    (value.size as number) >= 0
  )
}

function isFollowup(value: ObjectValue): value is ObjectValue & ReceiveFollowupPayload {
  const attachmentsValid =
    value.attachments === undefined ||
    value.attachments === null ||
    (Array.isArray(value.attachments) && value.attachments.every(isAttachment))
  return (
    isTarget(value.target) &&
    typeof value.text === 'string' &&
    (value.text.trim().length > 0 || (Array.isArray(value.attachments) && value.attachments.length > 0)) &&
    nonempty(value.operationId) &&
    (value.inputId === undefined || value.inputId === null || nonempty(value.inputId)) &&
    nonempty(value.turnId) &&
    attachmentsValid &&
    readSlackExecutionContext({ slackExecutionContext: value.slackExecutionContext }).kind !== 'invalid'
  )
}

function isStop(value: ObjectValue): value is ObjectValue & CancelAgentSessionPayload {
  return isTarget(value.target) && nonempty(value.sessionId) && nonempty(value.turnId) && nonempty(value.operationId)
}

function isSessionCommand(value: ObjectValue): value is ObjectValue & SessionCommandRequest {
  if (
    !nonempty(value.sessionId) ||
    !nonempty(value.runtime) ||
    !nonempty(value.runnerId) ||
    !nonempty(value.operationId) ||
    (value.command !== 'compact' && value.command !== 'reset') ||
    !nullableNonemptyString(value.runtimeSessionId) ||
    !nullableNonemptyString(value.workDir) ||
    !(
      value.expectedRuntimeSessionId === undefined ||
      value.expectedRuntimeSessionId === null ||
      nonempty(value.expectedRuntimeSessionId)
    ) ||
    !(value.projectId === undefined || value.projectId === null || nonempty(value.projectId))
  )
    return false
  return value.command === 'compact'
    ? nonempty(value.runtimeSessionId) && value.expectedRuntimeSessionId == null
    : (value.expectedRuntimeSessionId ?? null) === (value.runtimeSessionId ?? null)
}

function nullableNonemptyString(value: unknown): boolean {
  return value === undefined || value === null || nonempty(value)
}

function normalizeTarget(target: ObjectValue): ObjectValue {
  const binding = target.binding as ObjectValue
  return { ...target, binding: { ...binding, workDir: binding.workDir ?? null } }
}

function normalizeFollowup(params: ObjectValue): ReceiveFollowupPayload {
  return { ...params, target: normalizeTarget(params.target as ObjectValue) } as unknown as ReceiveFollowupPayload
}

function normalizeStop(params: ObjectValue): CancelAgentSessionPayload {
  return { ...params, target: normalizeTarget(params.target as ObjectValue) } as unknown as CancelAgentSessionPayload
}

function normalizeSessionCommand(params: ObjectValue): SessionCommandRequest {
  return {
    ...params,
    runtimeSessionId: params.runtimeSessionId ?? null,
    workDir: params.workDir ?? null,
    expectedRuntimeSessionId: params.expectedRuntimeSessionId ?? null,
    projectId: params.projectId ?? null,
  } as unknown as SessionCommandRequest
}

function isWorkflowStatus(value: ObjectValue): value is ObjectValue & ReceiveWorkflowRunStatusPayload {
  return nonempty(value.workflowRunId) && nonempty(value.status)
}
