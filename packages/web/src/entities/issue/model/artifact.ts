export interface WorkflowArtifactSummary {
  artifactId: string
  path: string
  kind: 'file' | 'directory'
  displayName?: string | null
  size?: number | null
  recordedAt: string
}

export interface WorkflowArtifact {
  artifactId: string
  workflowRunId: string
  actionAttemptId: string
  path: string
  kind: 'file' | 'directory'
  contentType?: string | null
  size?: number | null
  recordedAt: string
  displayName?: string | null
}

export interface WorkflowArtifactDirectoryEntry {
  relativePath: string
  size: number
  contentType?: string | null
}

export interface WorkflowArtifactDirectory extends Omit<WorkflowArtifact, 'kind'> {
  kind: 'directory'
  entries?: WorkflowArtifactDirectoryEntry[]
  totalSize?: number
}

export interface WorkflowTaskRequiredFile {
  path: string
  source: string
  canFetchContent: boolean
  markers?: string[]
}
