export interface GeneralConfig {
  agentTimeout: number
  maxConcurrentAgents: number
  pollInterval: number
}

export interface AgentRuntimeConfig {
  timeout: number
  stageTimeout: number
  taskTimeout: number
  maxConcurrent: number
  maxGracePeriods: number
  pollInterval: number
}

export interface SystemInfo {
  running: {
    version: string | null
    gitHash: string | null
    startedAt: string
  }
  source: {
    path: string | null
    branch: string | null
    head: string | null
    dirty: boolean
  }
  install: {
    mode: 'local-source' | 'binary' | 'unknown'
    serviceManager: string | null
    serverUnit: string | null
    runnerUnit: string | null
    reason: string | null
  }
  update: {
    status: 'up-to-date' | 'update-available' | 'dirty-source' | 'unsupported' | 'unknown'
    available: boolean
    reason: string | null
  }
  services: {
    server: string | null
    runner: string | null
  }
  paths: {
    db: string | null
    config: string | null
    opencode: string | null
    logs: string | null
  }
}

export interface SystemUpdateStartResponse {
  job: SystemUpdateStatus
}

export interface SystemUpdateLogEntry {
  at: string
  stage: string
  message: string
}

export interface SystemUpdateStatus {
  jobId: string
  status: string
  stage: string
  updateAvailable: boolean
  runningGitHash: string | null
  sourceHead: string | null
  sourcePath: string | null
  serverUnit: string | null
  runnerUnit: string | null
  reason: string | null
  logs: SystemUpdateLogEntry[]
  createdAt: string
  updatedAt: string
  completedAt: string | null
}

export interface SystemUpdateStatusEnvelope {
  hasJob: boolean
  job: SystemUpdateStatus | null
}

export interface WorkflowProfileInfo {
  id: string
  displayName: string
  description: string
  isDefault: boolean
}

export interface WorkflowProfileStageSummary {
  stage: string
  requiresApproval: boolean
  tasks: string[]
  checks: string[]
}

export interface WorkflowProfileDetail {
  id: string
  displayName: string
  description: string
  isDefault: boolean
  yaml: string
  stages: WorkflowProfileStageSummary[]
}

export type ModelBadge = 'free' | 'latest'

export interface Model {
  id: string
  name: string
  badges: ModelBadge[]
  contextWindow: number
}
