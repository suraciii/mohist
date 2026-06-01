export interface RunnerScope {
  type: 'global' | 'project'
  projectId?: string | null
  projectName?: string | null
}

export interface RunnerCapacity {
  usedSlots: number
  totalSlots: number
}

export interface RunnerActiveWork {
  workId: string
  workflowRunId: string
  workType?: string | null
  stage?: string | null
  title?: string | null
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
  capacity?: RunnerCapacity | null
  activeWork?: RunnerActiveWork | null
}

export interface RunnerStatusListResponse {
  runners: RunnerStatusRow[]
}

export interface RunnerStatusSummary {
  connectedIdleCount: number
  connectedBusyCount: number
  hasConnectedCapacity: boolean
  rows: RunnerStatusRow[]
}