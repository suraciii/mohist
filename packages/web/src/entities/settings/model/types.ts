export interface GeneralConfig {
  agentTimeout: number
  maxConcurrentAgents: number
  pollInterval: number
  logLevel: string
  taskTimeout: number
  stageTimeout: number
  maxGracePeriods: number
}

export interface AgentRuntimeConfig {
  timeout: number
  stageTimeout: number
  taskTimeout: number
  maxConcurrent: number
  maxGracePeriods: number
  pollInterval: number
}

export type AgentRuntime = 'opencode' | 'pi'

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

export type SystemUpdateOutcome = 'succeeded' | 'recovered' | 'failed' | 'cancelled'

export const SYSTEM_UPDATE_OUTCOMES: readonly SystemUpdateOutcome[] = ['succeeded', 'recovered', 'failed', 'cancelled'] as const

export const SYSTEM_UPDATE_STAGES: readonly string[] = [
  'Building',
  'Restarting server',
  'Waiting for reconnect',
  'Restoring runner',
  'Verifying runtime',
] as const

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
  outcome?: SystemUpdateOutcome | null
  unavailableCapability?: string | null
}

export interface SystemUpdateStatusEnvelope {
  hasJob: boolean
  job: SystemUpdateStatus | null
}

export interface RuntimeConsistencyComponent {
  name: string
  status: string
  reason: string | null
}

export interface RuntimeConsistencyResponse {
  status: 'consistent' | 'inconsistent'
  reason: string | null
  components: RuntimeConsistencyComponent[]
}

export interface WorkflowProfileInfo {
  id: string
  displayName: string
  description: string
  isDefault: boolean
  isBuiltIn?: boolean
  agentAction?: string | null
  agentRuntime?: AgentRuntime | null
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
  projectId?: string
  sourceProvenance?: string
  isBuiltIn?: boolean
  definitionSource?: string | null
  yaml: string
  stages: WorkflowProfileStageSummary[]
  agentAction?: string | null
  agentRuntime?: AgentRuntime | null
}

export interface ActionCatalogEntry {
  name: string
  description?: string | null
  capabilities?: string[] | null
}

export interface ActionCatalog {
  actions: ActionCatalogEntry[]
}

export type ModelBadge = 'free' | 'latest'

export interface Model {
  id: string
  name: string
  badges: ModelBadge[]
  contextWindow: number
}
