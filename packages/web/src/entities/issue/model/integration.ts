export interface IntegrationStepResult {
  step: string
  status: 'completed' | 'failed'
  output?: unknown
  startedAt: string
  completedAt: string
  duration: number
}

export interface OpenSpecSyncConflict {
  capability: string
  type: string
  detail: string
  requirementHeader?: string
}

export interface OpenSpecSyncCounts {
  added: number
  modified: number
  removed: number
  renamed: number
}

export interface OpenSpecSyncOutput {
  capabilities: string[]
  targetFiles: string[]
  counts: OpenSpecSyncCounts
  conflicts: OpenSpecSyncConflict[]
  valid: boolean
  errors?: string[]
}

export interface MergeReadinessOutput {
  targetBranch: string
  canFastForward: boolean
  cleanRebaseFeasible: boolean
  conflictFiles?: string[]
}

export interface IntegrationHealthCheckPolicy {
  policyName: string
  command: string
  timeout: number
  enabled: boolean
}

export interface CheckReadinessOutput {
  specImpact?: OpenSpecSyncOutput
  mergeReadiness?: MergeReadinessOutput
  healthCheckPolicy?: IntegrationHealthCheckPolicy
}

export interface IntegrationFailureOutput {
  failingStep: string
  capability?: string
  conflictedFiles?: string[]
  requirementHeader?: string
  mergeReason?: string
  healthCommand?: string
  healthSummary?: string
  healthLogExcerpt?: string
  nextAction: string
}
