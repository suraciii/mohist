import type { CheckRepairState, StageCheckState, StageStateRead, StageStateStatus, StageTaskCause, StageTaskState, StageApprovalState, WorkflowCheckFailurePolicy, WorkflowRun, WorkflowStageDefinition } from './types'

const CAUSE_TYPE_MAP: Record<string, StageTaskCause['type']> = {
  'check-failure': 'check-failure',
  'health-gate-failure': 'health-gate-failure',
  'retry': 'retry',
  'rebase': 'rebase',
  'merge-conflict': 'merge-conflict',
  'unknown': 'unknown',
  'task-failure': 'check-failure',
  'branch-changed': 'rebase',
  'conflict': 'merge-conflict',
  'user-action': 'unknown',
  'system-policy': 'unknown',
}

export function mapCauseType(type: string): StageTaskCause['type'] {
  return CAUSE_TYPE_MAP[type] ?? 'unknown'
}

export function convertCause(cause: { type: string; checkName?: string; taskId?: string; message?: string } | null): StageTaskCause | undefined {
  if (!cause) return undefined
  return { type: mapCauseType(cause.type), checkName: cause.checkName, taskId: cause.taskId, message: cause.message }
}

function isTaskAttemptForBase(taskId: string, baseTaskId: string): boolean {
  return taskId === baseTaskId || taskId.startsWith(`${baseTaskId}:`)
}

function extractUnresolvedSummary(output: unknown, message: string | null): string | null {
  if (!output || typeof output !== 'object') return message ?? null
  const data = output as Record<string, unknown>
  if (typeof data.unresolvedSummary === 'string' && data.unresolvedSummary.length > 0) return data.unresolvedSummary
  if (data.verdict === 'FAIL' && typeof data.summary === 'string' && data.summary.length > 0) return data.summary
  if (typeof data.message === 'string' && data.message.length > 0) return data.message
  return message ?? null
}

function checkRepairPolicy(definition?: WorkflowStageDefinition | null, failedCheckName?: string | null): WorkflowCheckFailurePolicy | null {
  if (!definition?.checkFailurePolicies?.length) return null
  if (failedCheckName) {
    const exact = definition.checkFailurePolicies.find(policy => policy.checkName === failedCheckName)
    if (exact) return exact
  }
  return definition.checkFailurePolicies[0] ?? null
}

function computeCheckRepair(tasks: StageTaskState[], checks: StageCheckState[], definition?: WorkflowStageDefinition | null): CheckRepairState | undefined {
  const failedCheckName = checks.find(check => check.status === 'failed')?.checkName ?? null
  const policy = checkRepairPolicy(definition, failedCheckName)
  if (!policy) return undefined

  const repairTasks = tasks.filter(task => isTaskAttemptForBase(task.taskId, policy.fixTaskId))
  const verdictCheck = checks.find(check => check.checkName === policy.checkName)
  if (repairTasks.length === 0 && (!verdictCheck || verdictCheck.status === 'pending' || verdictCheck.status === 'running')) {
    return undefined
  }

  const maxAttempts = policy.maxAttempts
  const attemptsUsed = repairTasks.length
  const attemptsRemaining = Math.max(0, maxAttempts - attemptsUsed)
  const pendingRepairTasks = repairTasks.filter(task => task.status === 'pending' || task.status === 'running')
  const completedRepairTasks = repairTasks.filter(task => task.status === 'completed')
  const repairInProgress = pendingRepairTasks.length > 0
  const repairAvailable = attemptsRemaining > 0 && verdictCheck?.status === 'failed' && !repairInProgress

  let status: CheckRepairState['status']
  if (verdictCheck?.status === 'passed') status = 'not-needed'
  else if (repairInProgress) status = pendingRepairTasks.some(task => task.status === 'running') ? 'running' : 'pending'
  else if (completedRepairTasks.length > 0) {
    status = verdictCheck?.status === 'failed'
      ? repairAvailable ? 'available' : 'exhausted'
      : 'completed'
  }
  else status = repairAvailable ? 'available' : 'exhausted'

  let stopReason: CheckRepairState['stopReason'] = null
  if (verdictCheck?.status === 'passed') stopReason = 'review-passed'
  else if (pendingRepairTasks.some(task => task.status === 'pending')) stopReason = 'repair-pending'
  else if (pendingRepairTasks.some(task => task.status === 'running')) stopReason = 'repair-running'
  else if (attemptsRemaining === 0 && completedRepairTasks.length > 0) stopReason = 'max-repair-attempts-reached'

  const lastRepairTask = repairTasks.at(-1) ?? null
  return {
    checkName: policy.checkName,
    fixTaskId: policy.fixTaskId,
    status,
    attemptsUsed,
    attemptsMax: maxAttempts,
    attemptsRemaining,
    repairAvailable,
    lastRepairTask,
    lastRepairStatus: lastRepairTask?.status ?? null,
    followUpReviewStatus: verdictCheck?.status ?? null,
    stopReason,
    unresolvedSummary: verdictCheck?.status === 'failed' ? extractUnresolvedSummary(verdictCheck.output, verdictCheck.message) : null,
  }
}

export function workflowRunToStageStateMap(workflowRun: WorkflowRun): Map<string, StageStateRead> {
  const map = new Map<string, StageStateRead>()
  if (!workflowRun.stageRuns) return map

  for (const sr of workflowRun.stageRuns) {
    const tasks: StageTaskState[] = sr.tasks.map((t) => ({
      taskId: t.taskId,
      title: t.title,
      status: t.status,
      order: t.taskOrder ?? 0,
      attempts: t.attempts,
      duration: t.duration,
      artifacts: t.artifacts,
      output: t.output,
      startedAt: t.startedAt,
      completedAt: t.completedAt,
      updatedAt: new Date().toISOString(),
      reason: t.reason ?? undefined,
      causedBy: convertCause(t.causedBy),
      origin: t.origin ?? null,
    }))
    const checks: StageCheckState[] = sr.checks.map((c) => ({
      checkName: c.checkName,
      title: c.title,
      status: c.status,
      message: c.message,
      output: c.output,
      runCount: c.runCount,
      lastRunAt: c.lastRunAt,
      updatedAt: new Date().toISOString(),
      origin: c.origin ?? null,
    }))

    const approval: StageApprovalState | null = sr.approvalStatus
      ? {
          status: sr.approvalStatus,
          output: sr.approvalOutput,
          requestedAt: sr.approvalRequestedAt,
          respondedAt: sr.approvalRespondedAt,
        }
      : null

    const stageState: StageStateRead = {
      stage: sr.stage,
      status: sr.status as StageStateStatus,
      tasks,
      checks,
      approval,
      attempts: sr.attempts ?? 0,
      startedAt: sr.startedAt,
      completedAt: sr.completedAt,
      updatedAt: sr.updatedAt ?? new Date().toISOString(),
      failure: sr.failure ?? null,
      deliveryMetadata: sr.deliveryMetadata ?? null,
    }
    const definition = sr.definition ?? workflowRun.workflowDefinition?.stageDefinitions?.find(candidate => candidate.stage === sr.stage) ?? null
    const checkRepair = computeCheckRepair(tasks, checks, definition)
    if (checkRepair) stageState.checkRepair = checkRepair
    map.set(sr.stage, stageState)
  }
  return map
}
