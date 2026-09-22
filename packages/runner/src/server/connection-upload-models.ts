export interface ArtifactUploadRequest {
  path: string
  /** `'directory'` selects the dedicated directory upload route. */
  kind?: 'file' | 'directory'
  contentType?: string | null
  contentHash?: string | null
  size: number
  content: Uint8Array
  filename?: string
}

export interface ArtifactUploadResponse {
  uploadId: string
  workflowRunId: string
  workId: string
  actionAttemptId: string | null
  path: string
  contentType: string | null
  contentHash: string | null
  size: number
  createdAt: string | null
  expiresAt: string | null
  idempotent: boolean
}

export interface TaskLogUploadResult {
  status: 'changed' | 'duplicate'
  accepted: number
  truncated: boolean
}
