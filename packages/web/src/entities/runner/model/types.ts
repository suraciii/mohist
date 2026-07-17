export interface RunnerScope {
  type: 'global' | 'project'
  projectId?: string | null
  projectName?: string | null
}

export interface RunnerCapacity {
  usedSlots: number
  totalSlots: number
}

export interface RunnerActiveWorkIssueRef {
  projectId: string
  issueNumber: number
}

export interface RunnerActiveWork {
  workId: string
  ownerKind: string
  ownerId: string
  workType: string
  stage?: string | null
  title?: string | null
  issue?: RunnerActiveWorkIssueRef | null
}

export interface RunnerStatusRow {
  id: string
  kind: string
  hostname: string
  scope: RunnerScope
  status: 'idle' | 'busy' | 'stale' | 'offline'
  registeredAt?: string | null
  lastHeartbeatAt?: string | null
  connectionState?: string | null
  capabilities: string[]
  coderModels: string[]
  coderModelCount: number
  maxWorkflowSlots?: number | null
  buildGitHash?: string | null
  capacity?: RunnerCapacity | null
  activeWorks: RunnerActiveWork[]
}

export interface RunnerStatusListResponse {
  runners: RunnerStatusRow[]
}

export interface RunnerStatusDetailResponse {
  runner: RunnerStatusRow
}

export interface RunnerStatusSummary {
  connectedIdleCount: number
  connectedBusyCount: number
  hasConnectedCapacity: boolean
  rows: RunnerStatusRow[]
}
