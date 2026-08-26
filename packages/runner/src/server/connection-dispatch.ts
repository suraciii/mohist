import { parseObject } from '../core/json.js'
import type { DispatchWorkItem, WorkDispatchResponse } from '../core/types.js'

export function parseDispatchWorkItem(dispatch: WorkDispatchResponse): DispatchWorkItem {
  const work: DispatchWorkItem = {
    workflowRunId: dispatch.workflowRunId,
    workId: dispatch.workId,
    taskRunId: dispatch.taskRunId ?? undefined,
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
    recoveryGeneration: dispatch.recoveryGeneration ?? undefined,
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
