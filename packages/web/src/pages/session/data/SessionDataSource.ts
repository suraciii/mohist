import type { SessionTurn, AgentSessionTranscriptResponse, SessionMetadata, SessionStatusKind, RuntimeSessionLineageEntry } from '../../../entities/coder-session'
import type { DisplayTurn } from '../../../widgets/session-transcript'

export type StatusKind = SessionStatusKind

export interface SessionCancelOptions {
  onSettled?: () => void
}

export interface SessionDataSourceResult {
  isLoading: boolean
  isError: boolean
  notFound: boolean

  sessionKey: string
  runtimeSessionId: string
  meta: SessionMetadata | null
  transcriptResponse: AgentSessionTranscriptResponse | null
  initialTurns: SessionTurn[]

  statusKind: StatusKind
  isRunning: boolean
  canFollowup?: boolean

  followupIsPending: boolean
  sendFollowup: (text: string) => void

  cancel: {
    mutate: (options?: SessionCancelOptions) => void
    isPending: boolean
  } | null

  contextWindowUsed: number | null
  contextWindowSize: number | null
  contextUsagePercent: number | null
  healthStatus: string | null

  hasRecoveryActions: boolean
  recoverySessionName: string | null
  recoverySessionId?: string | null
  runtimeSessionLineage: RuntimeSessionLineageEntry[] | null
  viewedRuntimeSessionId: string | null
  buildLineageTargetPath: ((runtimeId: string) => string) | null

  metadataQueryKey: readonly unknown[]
  transcriptQueryKey: readonly unknown[]
  handleRecoverySuccess: () => void

  backPath: string
  backLabel: string
  issueTitle?: string
  /**
   * Project-scoped entry point to the workflow context. For issue-bound
   * sessions this links to the issue detail page (which hosts the
   * WorkflowSessionsPanel); for sessions with no issue binding the field
   * is undefined so the entry point is not fabricated.
   */
  workflowContextPath?: string
  workflowContextLabel?: string

  siblingNav: React.ReactNode | null
  siblingSidebar: React.ReactNode | null

  sessionTurns: SessionTurn[]
  transcriptVersion: number
  scrollToBottom: () => void
  newContentAvailable: boolean
  setIsNearBottom: (v: boolean) => void
  isFinalizing: boolean
  isThinking: boolean
  isStreaming: boolean

  displayTurns: DisplayTurn[]

  issueNumber: number
}
