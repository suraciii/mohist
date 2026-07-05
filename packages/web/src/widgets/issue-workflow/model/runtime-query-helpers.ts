import type { WorkflowTimeline } from '../../../entities/issue'

export function isScriptHealthCheck(
  check: { name?: string; checkName?: string; output?: unknown; status?: string; message?: string | null } | undefined | null,
): boolean {
  if (!check) return false
  if (check.name === 'health') return true
  if (check.checkName === 'health') return true
  const output = check.output as { kind?: string } | undefined
  if (output?.kind === 'script') return true
  return false
}

function isFailedCheck(check: { status?: string; message?: string | null } | undefined | null): boolean {
  if (!check || !check.status) return false
  const status = check.status.toLowerCase()
  return status === 'failed' || status === 'error'
}

export function findFailedScriptHealthCheck(
  timeline: Pick<WorkflowTimeline, 'currentStage' | 'status' | 'stages' | 'pendingWork' | 'availableActions'> | null | undefined,
): boolean {
  if (!timeline?.stages) return false
  for (const stage of timeline.stages) {
    if (!stage.checks) continue
    for (const check of stage.checks) {
      if (isScriptHealthCheck(check) && isFailedCheck(check)) {
        return true
      }
    }
  }
  return false
}

export function findRunningCheck(
  timeline: Pick<WorkflowTimeline, 'currentStage' | 'status' | 'stages' | 'pendingWork' | 'availableActions'> | null | undefined,
): { title: string; status: string } | null {
  if (!timeline?.stages) return null
  for (const stage of timeline.stages) {
    if (!stage.checks) continue
    for (const check of stage.checks) {
      const status = (check.status ?? '').toLowerCase()
      if (status === 'running') {
        return { title: check.title || check.name, status: check.status ?? 'running' }
      }
    }
  }
  return null
}

export function findRunningTask(
  timeline: Pick<WorkflowTimeline, 'currentStage' | 'status' | 'stages' | 'pendingWork' | 'availableActions'> | null | undefined,
): { title: string; status: string } | null {
  if (!timeline?.stages) return null
  for (const stage of timeline.stages) {
    if (!stage.tasks) continue
    for (const task of stage.tasks) {
      const status = (task.status ?? '').toLowerCase()
      if (status === 'running') {
        return { title: task.title, status: task.status ?? 'running' }
      }
    }
  }
  return null
}

export function findFailedCheck(
  timeline: Pick<WorkflowTimeline, 'currentStage' | 'status' | 'stages' | 'pendingWork' | 'availableActions'> | null | undefined,
): { title: string; status: string } | null {
  if (!timeline?.stages) return null
  for (const stage of timeline.stages) {
    if (!stage.checks) continue
    for (const check of stage.checks) {
      const status = (check.status ?? '').toLowerCase()
      if (status === 'failed' || status === 'error') {
        return { title: check.title || check.name, status: check.status ?? 'failed' }
      }
    }
  }
  return null
}

export function formatStageLabel(stage: string | null | undefined): string {
  if (!stage) return 'workflow'
  return stage
    .split(/[_-]/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ')
}