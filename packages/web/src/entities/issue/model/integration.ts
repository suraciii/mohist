export interface IntegrationStepResult {
  step: string
  status: 'completed' | 'failed'
  output?: unknown
  startedAt: string
  completedAt: string
  duration: number
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
  mergeReadiness?: MergeReadinessOutput
  healthCheckPolicy?: IntegrationHealthCheckPolicy
}

export interface IntegrationFailureOutput {
  failingStep: string
  conflictedFiles?: string[]
  mergeReason?: string
  healthCommand?: string
  healthSummary?: string
  healthLogExcerpt?: string
  nextAction: string
}
