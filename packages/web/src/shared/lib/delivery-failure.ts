export type DeliveryFailureKind =
  | 'conflict'
  | 'base-moved'
  | 'retry-safe'
  | 'branch-invariant-violation'
  | 'workspace-setup'
  | 'config-error'
  | 'protection-conflict'
  | 'pr-state-conflict'

export interface DeliveryFailureGuidance {
  failureKind: DeliveryFailureKind
  label: string
  nextAction: string
  retryable: boolean
}

const DELIVERY_FAILURE_GUIDANCE: Record<DeliveryFailureKind, DeliveryFailureGuidance> = {
  conflict: { failureKind: 'conflict', label: 'Conflict needs attention', nextAction: 'Resolve the conflicting files on the issue branch, then rerun prepare.', retryable: false },
  'base-moved': { failureKind: 'base-moved', label: 'Base branch moved', nextAction: 'Prepare the branch again, then publish.', retryable: true },
  'retry-safe': { failureKind: 'retry-safe', label: 'Transient failure', nextAction: 'Retry the task.', retryable: true },
  'branch-invariant-violation': { failureKind: 'branch-invariant-violation', label: 'Runner / action branch-invariant violation', nextAction: 'Retry the task and report the runner/action failure if it recurs.', retryable: true },
  'workspace-setup': { failureKind: 'workspace-setup', label: 'Workflow workspace setup failure', nextAction: 'Check repository and runner workspace configuration, then retry.', retryable: false },
  'config-error': { failureKind: 'config-error', label: 'Runner environment is misconfigured', nextAction: 'Correct the runner environment before rerunning.', retryable: false },
  'protection-conflict': { failureKind: 'protection-conflict', label: 'Branch protection blocked the merge', nextAction: 'Resolve the repository protection requirement, then rerun.', retryable: false },
  'pr-state-conflict': { failureKind: 'pr-state-conflict', label: 'Pull request state changed externally', nextAction: 'Review the pull request state, then rerun or close the issue.', retryable: false },
}

export function isDeliveryFailureKind(value: unknown): value is DeliveryFailureKind {
  return typeof value === 'string' && value in DELIVERY_FAILURE_GUIDANCE
}

export function getDeliveryFailureGuidance(code: unknown): DeliveryFailureGuidance | null {
  return isDeliveryFailureKind(code) ? DELIVERY_FAILURE_GUIDANCE[code] : null
}
