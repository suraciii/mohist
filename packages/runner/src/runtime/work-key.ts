import type { DispatchReportOwner, DispatchWorkItem } from '../core/types.js'

export function workKey(work: DispatchWorkItem, reportOwner?: DispatchReportOwner): string {
  const ownerKind = reportOwner?.ownerKind ?? normalizedOwnerKind(work.ownerKind)
  const ownerId = reportOwner
    ? ownerKind === 'agent-job'
      ? reportOwner.agentJobId
      : reportOwner.workflowRunId
    : ownerKind === 'agent-job'
      ? work.agentJobId
      : work.workflowRunId
  if (ownerKind !== 'workflow' && ownerKind !== 'agent-job') return `invalid-owner:${work.workId}`
  if (typeof ownerId !== 'string' || ownerId.trim().length === 0) return `invalid-owner:${work.workId}`
  return `${ownerKind}:${ownerId}:${work.workId}`
}

function normalizedOwnerKind(value: string | null | undefined): string | null {
  const ownerKind = value?.trim().toLowerCase()
  return ownerKind === 'workflow' || ownerKind === 'agent-job' ? ownerKind : null
}
