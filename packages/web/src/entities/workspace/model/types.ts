export type WorkspaceStatus = 'active' | 'archived'

export interface WorkspaceOrigin {
  kind: 'issue' | 'slack' | 'web' | 'cli' | 'manual' | 'unknown'
  issueNumber?: number | null
  teamId?: string | null
  channelId?: string | null
  conversationId?: string | null
}

export interface WorkspaceHome {
  runnerId: string
  path: string
}

export interface WorkspaceDirectoryObservation {
  attemptId: string
  runnerId: string
  homePath: string
  outcome: 'removed' | 'already_absent' | 'in_use' | 'unsafe' | 'deletion_failed' | 'unknown'
  observedAt: string
  reason?: string | null
  estimatedBytes?: number | null
  measuredAt?: string | null
}

export interface WorkspaceSession {
  id: string
  source: string
  runtimeSessionId?: string | null
  runtime?: string | null
  activity: string
  createdAt: string
  lastActivityAt?: string | null
  model?: string | null
  agentId?: string | null
  agentName?: string | null
  workflowRunId?: string | null
  sessionName?: string | null
  origin?: string | null
  targetId?: string | null
}

export interface Workspace {
  projectId: string
  name: string
  origin: WorkspaceOrigin
  repositories: string[]
  status: WorkspaceStatus
  home: WorkspaceHome | null
  createdAt: string
  archivedAt?: string | null
  boundSessionCount: number
  sessions?: WorkspaceSession[] | null
  directory?: WorkspaceDirectoryObservation | null
}
