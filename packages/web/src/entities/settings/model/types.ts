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
  version: string
  gitHash: string
  sourceHead: string | null
  server: {
    host: string
    port: number
    status: 'running'
  }
  paths: {
    db: string
    config: string
    opencode: string | null
    logs: string
  }
}

export type ModelBadge = 'free' | 'latest'

export interface Model {
  id: string
  name: string
  badges: ModelBadge[]
  contextWindow: number
}
