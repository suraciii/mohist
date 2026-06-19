export interface ApprovalState {
  status: 'pending' | 'awaiting' | 'approved' | 'rejected' | 'error'
  stage?: string
  output?: Record<string, unknown>
  requestedAt: string
  respondedAt?: string
}

export type ApprovalFeedbackStatus = 'open' | 'resolved'

export interface ApprovalFeedbackResolution {
  resolutionTaskId?: string | null
  resolvedAt?: string | null
  resolutionSummary?: string | null
}

export interface ApprovalFeedback {
  id: string
  issueNumber?: number
  workflowRunId: string
  stage: string
  status: ApprovalFeedbackStatus
  body: string
  createdAt: string
  resolution?: ApprovalFeedbackResolution | null
}

export type ApprovalArtifact = {
  type: string
  path: string
  content: string
}

export type ApprovalOutput = {
  summary?: string
  artifacts?: ApprovalArtifact[]
  [key: string]: unknown
}
