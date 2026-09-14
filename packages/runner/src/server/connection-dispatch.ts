import { parseObject, isObject } from '../core/json.js'
import { stringAt } from '../core/json-path.js'
import type { DispatchWorkItem, WorkDispatchResponse, WorkItemResult } from '../core/types.js'

export function validateDispatchEnvelope(work: DispatchWorkItem): void | WorkItemResult {
  const ownerKind = work.ownerKind
  if (ownerKind !== 'workflow' && ownerKind !== 'agent-job') {
    return invalidDispatch('ownerKind', 'ownerKind must be workflow or agent-job')
  }

  if (ownerKind === 'workflow' && !nonEmptyString(work.workflowRunId)) {
    return invalidDispatch('workflowRunId', 'workflowRunId is required for workflow-owned work')
  }
  if (ownerKind === 'workflow' && work.agentJobId != null) {
    return invalidDispatch('agentJobId', 'agentJobId must be absent for workflow-owned work')
  }
  if (ownerKind === 'agent-job' && !nonEmptyString(work.agentJobId)) {
    return invalidDispatch('agentJobId', 'agentJobId is required for agent-job-owned work')
  }
  if (!nonEmptyString(work.projectId)) return invalidDispatch('projectId')

  const payload = work.with
  if (ownerKind === 'agent-job') {
    const source = payload?.['executionSource']
    if (source !== 'slack' && source !== 'non-slack') {
      return invalidDispatch('executionSource', 'executionSource must be slack or non-slack')
    }
  }

  if (ownerKind === 'agent-job') {
    const runtime = declaredAgentRuntime(work)
    if (runtime === null) return invalidDispatch('runtime', 'runtime must be opencode or pi')
  } else if (!isChecksDispatch(work) && !nonEmptyString(work.uses)) {
    return invalidDispatch('runtime', 'workflow dispatch must resolve a runtime from uses')
  }

  const workspace = isObject(work.variables?.['workspace']) ? work.variables['workspace'] : null
  if (workspace && nonEmptyString(workspace['name']) && nonEmptyString(work.workflowRunId)) {
    const missingRepositoryField = ['name', 'gitUrl', 'baseBranch'].find(
      (field) => !nonEmptyString(stringAt(work.variables ?? {}, ['repository', field])),
    )
    if (missingRepositoryField) {
      return invalidDispatch(
        `repository.${missingRepositoryField}`,
        `named workspace requires repository.${missingRepositoryField}`,
      )
    }
  }

  return undefined
}

export function parseDispatchWorkItem(dispatch: WorkDispatchResponse): DispatchWorkItem {
  const work: DispatchWorkItem = {
    workflowRunId: dispatch.workflowRunId,
    workId: dispatch.workId,
    actionAttemptId: dispatch.actionAttemptId ?? undefined,
    workType: dispatch.workType,
    stage: dispatch.stage,
    title: dispatch.title,
    uses: dispatch.uses,
    with: parseObject(dispatch.with),
    expect: parseObject(dispatch.expect),
    variables: parseObject(dispatch.variables),
    projectId: dispatch.projectId,
    issueNumber: dispatch.issueNumber ?? undefined,
    epicNumber: dispatch.epicNumber ?? undefined,
    artifacts: parseObject(dispatch.artifacts),
    setVars: dispatch.setVars ? (parseObject(dispatch.setVars) as Record<string, string> | null) : null,
    ownerKind: dispatch.ownerKind ?? undefined,
    agentJobId: dispatch.agentJobId ?? undefined,
    agentSessionId: dispatch.agentSessionId ?? undefined,
    recovery: parseObject(dispatch.recovery),
    agentDefinition: dispatch.agentDefinition ?? undefined,
    agentSessionStartup: dispatch.agentSessionStartup ?? undefined,
  }
  if (Object.prototype.hasOwnProperty.call(dispatch, 'parentIssueContext'))
    work.parentIssueContext = dispatch.parentIssueContext
  if (Object.prototype.hasOwnProperty.call(dispatch, 'recoveryRemaining'))
    work.recoveryRemaining = dispatch.recoveryRemaining
  if (Object.prototype.hasOwnProperty.call(dispatch, 'initialInputId'))
    work.initialInputId = dispatch.initialInputId ?? undefined
  if (Object.prototype.hasOwnProperty.call(dispatch, 'initialTurnId'))
    work.initialTurnId = dispatch.initialTurnId ?? undefined
  if (dispatch.capabilityRevision != null) work.capabilityRevision = dispatch.capabilityRevision ?? undefined
  return work
}

function declaredAgentRuntime(work: DispatchWorkItem): 'opencode' | 'pi' | null {
  const payload = work.with
  const hasPayloadRuntime =
    payload !== null && payload !== undefined && Object.prototype.hasOwnProperty.call(payload, 'runtime')
  const value = hasPayloadRuntime ? payload?.['runtime'] : work.agentDefinition?.runtime
  if (typeof value !== 'string') return null
  const normalized = value.trim().toLowerCase()
  return normalized === 'opencode' || normalized === 'pi' ? normalized : null
}

function isChecksDispatch(work: DispatchWorkItem): boolean {
  return work.workType.trim().toLowerCase() === 'checks'
}

function nonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}

function invalidDispatch(field: string, detail = `${field} is required`): WorkItemResult {
  return {
    status: 'failed',
    message: detail,
    error: { code: 'invalid-dispatch', message: detail },
  }
}
