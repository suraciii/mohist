import { parseObject, isObject } from '../core/json.js'
import { stringAt } from '../core/json-path.js'
import { readExecutionSourceContext } from '../runtime/slack-execution-context.js'
import type {
  DispatchReportOwner,
  DispatchWorkItem,
  ManagerExecutionGrantResponse,
  PolledDispatch,
  WorkDispatchResponse,
  WorkItemResult,
} from '../core/types.js'

const MANAGER_PROJECT_ID = '__mohist_slack_manager__'
const MANAGER_ORIGIN_MARKER = 'slack-manager'

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

  const sourcePayload = ownerKind === 'agent-job' ? work.with : work.variables
  const sourceContext = readExecutionSourceContext(sourcePayload ?? null)
  if (sourceContext.kind === 'invalid') {
    const field = sourcePayload?.['executionSource'] === 'slack' ? 'slackExecutionContext' : 'executionSource'
    return invalidDispatch(field, `${field} is invalid: ${sourceContext.message}`)
  }

  if (ownerKind === 'agent-job') {
    const runtime = declaredAgentRuntime(work)
    if (runtime === null) return invalidDispatch('runtime', 'runtime must be opencode or pi')
  } else {
    const runtimeFailure = validateWorkflowRuntime(work)
    if (runtimeFailure) return runtimeFailure
  }

  const workspace = isObject(work.variables?.['workspace']) ? work.variables['workspace'] : null
  const managerDispatch = ownerKind === 'agent-job' && work.projectId === MANAGER_PROJECT_ID
  if (!workspace && !managerDispatch) return invalidDispatch('workspace', 'dispatch requires a workspace binding')
  if (workspace) {
    const namedWorkspace = nonEmptyString(workspace['name'])
    const freePath = nonEmptyString(workspace['path'])
    if (!namedWorkspace && !freePath) {
      return invalidDispatch('workspace', 'workspace.name or workspace.path must be a non-empty string')
    }
    if (namedWorkspace && nonEmptyString(work.workflowRunId)) {
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
  }

  return undefined
}

/** Parse one wire dispatch without allowing Manager metadata to escape its work item. */
export function parsePolledDispatch(dispatch: WorkDispatchResponse): PolledDispatch {
  const work = parseDispatchWorkItem(dispatch)
  const metadata = parseManagerMetadata(work, dispatch)
  const reportOwner = parseReportOwner(dispatch.reportOwner)
  return {
    work,
    ...(reportOwner ? { reportOwner } : {}),
    ...(metadata.grant ? { managerExecutionGrant: metadata.grant } : {}),
    ...(metadata.originMarker !== undefined ? { originMarker: metadata.originMarker } : {}),
    ...(metadata.failure ? { validationFailure: metadata.failure } : {}),
  }
}

function parseReportOwner(value: unknown): DispatchReportOwner | undefined {
  if (!isObject(value)) return undefined
  const ownerKind = value['ownerKind']
  if (ownerKind !== 'workflow' && ownerKind !== 'agent-job') return undefined
  const workflowRunId = value['workflowRunId']
  const agentJobId = value['agentJobId']
  const ownerId = ownerKind === 'agent-job' ? agentJobId : workflowRunId
  if (!nonEmptyString(ownerId)) return undefined
  return {
    ownerKind,
    ...(typeof workflowRunId === 'string' ? { workflowRunId } : {}),
    ...(typeof agentJobId === 'string' ? { agentJobId } : {}),
  }
}

function parseManagerMetadata(
  work: DispatchWorkItem,
  dispatch: WorkDispatchResponse,
): {
  grant?: ManagerExecutionGrantResponse
  originMarker?: string | null
  failure?: WorkItemResult
} {
  const rawGrant: unknown = dispatch.managerExecutionGrant
  const rawOrigin: unknown = dispatch.originMarker
  const hasGrant = rawGrant !== undefined && rawGrant !== null
  const hasOrigin = rawOrigin !== undefined && rawOrigin !== null
  const isManager = work.projectId === MANAGER_PROJECT_ID

  if (hasOrigin && (typeof rawOrigin !== 'string' || rawOrigin.trim().length === 0)) {
    return { failure: invalidDispatch('originMarker', 'originMarker must be a non-empty string when present') }
  }

  if (!isManager) {
    if (hasGrant || hasOrigin) {
      return {
        failure: invalidDispatch(
          'managerExecutionGrant',
          'Manager grant and originMarker may only be attached to Manager dispatches',
        ),
      }
    }
    return {}
  }

  if (!hasGrant) return { failure: invalidDispatch('managerExecutionGrant', 'Manager dispatch requires a grant') }
  if (!isManagerGrant(rawGrant))
    return { failure: invalidDispatch('managerExecutionGrant', 'Manager grant is malformed') }
  if (rawOrigin !== MANAGER_ORIGIN_MARKER) {
    return { failure: invalidDispatch('originMarker', 'Manager dispatch requires originMarker slack-manager') }
  }

  return {
    grant: rawGrant,
    originMarker: rawOrigin,
  }
}

function isManagerGrant(value: unknown): value is ManagerExecutionGrantResponse {
  if (!isObject(value)) return false
  if (
    !nonEmptyString(value['managementCredential']) ||
    !nonEmptyString(value['replyCredential']) ||
    !nonEmptyString(value['executionId']) ||
    !nonEmptyString(value['expiresAt']) ||
    !nonEmptyString(value['deploymentEpoch'])
  )
    return false
  return Number.isFinite(Date.parse(value['expiresAt']))
}

function declaredAgentRuntime(work: DispatchWorkItem): 'opencode' | 'pi' | null {
  const value = work.with?.['runtime']
  if (value !== 'opencode' && value !== 'pi') return null
  return value
}

function validateWorkflowRuntime(work: DispatchWorkItem): WorkItemResult | undefined {
  if (!isRuntimeDispatch(work)) return undefined
  return runtimeForWorkflow(work) === null
    ? invalidDispatch('runtime', 'workflow dispatch must resolve a runtime from uses')
    : undefined
}

function runtimeForWorkflow(work: DispatchWorkItem): 'opencode' | 'pi' | null {
  const uses = work.uses?.trim().toLowerCase()
  if (uses === 'mohist/opencode') return 'opencode'
  if (uses === 'mohist/pi') return 'pi'
  return null
}

function isRuntimeDispatch(work: DispatchWorkItem): boolean {
  const uses = work.uses?.trim().toLowerCase()
  return uses === 'mohist/opencode' || uses === 'mohist/pi'
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
