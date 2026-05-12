import type { StageTaskCause, StageTaskState, StageCheckState, StageStateRead, StageStateStatus, StageApprovalState, WorkflowRun } from './types'

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

export function workflowRunToStageStateMap(workflowRun: WorkflowRun): Map<string, StageStateRead> {
  const map = new Map<string, StageStateRead>()
  if (!workflowRun.stageRuns) return map

  for (const sr of workflowRun.stageRuns) {
    const tasks: StageTaskState[] = sr.tasks.map((t) => ({
      taskId: t.taskId,
      title: t.title,
      status: t.status,
      source: (t.reason || t.causedBy) ? 'dynamic' as const : 'static' as const,
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
    }))
    const checks: StageCheckState[] = sr.checks.map((c) => ({
      checkName: c.checkName,
      status: c.status,
      message: c.message,
      output: c.output,
      runCount: c.runCount,
      lastRunAt: c.lastRunAt,
      updatedAt: new Date().toISOString(),
    }))

    const approval: StageApprovalState | null = sr.approvalStatus
      ? {
          status: sr.approvalStatus,
          output: sr.approvalOutput,
          requestedAt: sr.approvalRequestedAt,
          respondedAt: sr.approvalRespondedAt,
        }
      : null

    map.set(sr.stage, {
      stage: sr.stage,
      status: sr.status as StageStateStatus,
      tasks,
      checks,
      approval,
      attempts: sr.attempts ?? 0,
      startedAt: sr.startedAt,
      completedAt: sr.completedAt,
      updatedAt: new Date().toISOString(),
    })
  }
  return map
}