import type { AgentSessionTranscriptResponse, FollowupStatus, SessionFollowupResult, SessionMetadata, SessionRecoveryObservation, SessionStatusKind, SessionTurn } from '../../../entities/coder-session'
import type { AgentLaunchObservationDto } from '../../../entities/agent'
import type {
  TimelineEntry,
  TimelineFact,
  TimelineItem,
  TimelineReference,
} from '../../../entities/session'
import type { SessionTimelineCurrentActivity } from '../../../widgets/session-transcript'

export type StatusKind = SessionStatusKind | 'live' | 'finalizing' | 'probing' | 'recovering' | 'completed' | 'failed' | 'stale'
export type EmptyStateKind = 'active-no-content' | 'idle-no-content' | 'unknown-no-content'

export interface SessionStopOptions {
  onSuccess?: (result: { state: string }) => void
  onSettled?: () => void
}

/**
 * Authoritative handle for the single stop operation against the canonical
 * Session-ID API. It is available for both queued and executing Turns;
 * the Server chooses local cancellation or fenced runtime delivery.
 */
export interface SessionTurnControlHandle {
  turnId: string
  state: 'queued' | 'executing'
  mutate: (options?: SessionStopOptions) => void
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
  transcriptView?: 'public' | 'raw'
  setTranscriptView?: (view: 'public' | 'raw') => void
  transcriptViewLoading?: boolean
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

  facts: TimelineFact[]
  items: TimelineItem[]
  entries: TimelineEntry[]
  currentActivity: SessionTimelineCurrentActivity
  resolveTimelineReference?: (reference: TimelineReference) => string | null

  emptyStateKind: EmptyStateKind | null

  issueNumber: number
}
