import type { AgentSessionTranscriptResponse, FollowupStatus, SessionFollowupResult, SessionMetadata, SessionRecoveryObservation, SessionStatusKind, SessionTurn } from '../../../entities/coder-session'
import type { AgentLaunchObservationDto } from '../../../entities/agent'
import type {
  TimelineEntry,
  TimelineFact,
  TimelineItem,
  TimelineReference,
} from '../../../entities/session'
import type { DisplayTurn, SessionTimelineCurrentActivity } from '../../../widgets/session-transcript'

export type StatusKind = SessionStatusKind | 'live' | 'finalizing' | 'probing' | 'completed' | 'failed' | 'stale'
export type EmptyStateKind = 'active-no-content' | 'idle-no-content' | 'unknown-no-content'

export interface SessionCancelOptions {
  onSuccess?: (result: { state: string }) => void
  onSettled?: () => void
}

/**
 * Authoritative handle for one turn-control operation against the
 * canonical Session-ID API. `cancel` is present only when the current
 * turn is queued; `stop` is present only when the current turn is
 * executing. Mutating either dispatches the matching Server command
 * (deterministic cancellation for queued, interrupt request for
 * executing) and invalidates the unified summary, transcript, and
 * Session-list queries through the data source.
 */
export interface SessionTurnControlHandle {
  turnId: string
  state: 'queued' | 'executing'
  mutate: (options?: SessionCancelOptions) => void
  isPending: boolean
}

export interface SessionDataSourceResult {
  isLoading: boolean
  isError: boolean
  notFound: boolean

  sessionKey: string
  runtimeSessionId: string
  runtimeSessionLineage?: unknown
  viewedRuntimeSessionId?: string | null
  buildLineageTargetPath?: ((runtimeSessionId: string) => string) | null
  historicalRuntimeTarget?: unknown
  historicalRuntimeId?: string | null
  meta: SessionMetadata | null
  transcriptResponse: AgentSessionTranscriptResponse | null
  launchObservation?: AgentLaunchObservationDto | null
  initialTurns: SessionTurn[]

  statusKind: StatusKind
  isRunning: boolean
  canFollowup?: boolean

  followupIsPending: boolean
  followupStatus?: FollowupStatus | null
  sendFollowup: (text: string, attachmentIds?: string[]) => Promise<SessionFollowupResult | void>
  supportsInputAttachments?: boolean
  projectId?: string | null

  cancel: SessionTurnControlHandle | null
  stop: SessionTurnControlHandle | null

  contextWindowUsed: number | null
  contextWindowSize: number | null
  contextUsagePercent: number | null
  healthStatus: string | null

  hasRecoveryActions: boolean
  recoveryAvailable?: boolean
  recoverySessionName: string | null
  recoverySessionId?: string | null
  recoveryHistory?: SessionRecoveryObservation[] | null

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

  facts?: TimelineFact[]
  items?: TimelineItem[]
  entries?: TimelineEntry[]
  currentActivity?: SessionTimelineCurrentActivity
  resolveTimelineReference?: (reference: TimelineReference) => string | null

  displayTurns: DisplayTurn[]

  emptyStateKind: EmptyStateKind | null

  issueNumber: number
}
