import { useEffect, useRef, useState, useCallback, type RefObject } from 'react'
import { Link } from 'react-router-dom'
import { ChevronLeftIcon, ChevronRightIcon, CircleStopIcon, AlertTriangleIcon, CheckIcon, CopyIcon } from 'lucide-react'
import {
  SessionTranscriptLayout as DefaultSessionTranscriptLayout,
  selectFailedToolCalls,
  selectToolCallGroupIds,
  useTranscriptLocate,
} from '../../../widgets/session-transcript'
import type { DisplayToolPart } from '../../../widgets/session-transcript'
import { SessionFollowupComposer as DefaultSessionFollowupComposer, SessionRecoveryActions as DefaultSessionRecoveryActions } from '../../../widgets/coder-session'
import { ContextHealthBar as DefaultContextHealthBar } from '../../../widgets/coder-session'
import { Button } from '@/shared/ui/components/button'
import { AlertDialog } from '@/shared/ui/components/alert-dialog'
import { formatSessionTime } from '@/shared/lib/format-time'
import { useMediaQuery } from '@/shared/lib/use-media-query'
import { agentInputAttachmentContentPath, getAgentLaunchObservationMeaning } from '../../../entities/agent'
import type { AgentTurnObservation, SessionInputObservation } from '../../../entities/coder-session'
import type { StatusKind, EmptyStateKind, SessionDataSourceResult } from '../data/SessionDataSource'
import { SessionUsageSummary } from './SessionUsageSummary'
import type { MarkdownAttachment } from '@/shared/ui/markdown-reader/MarkdownReader'

export interface SessionDetailShellComponents {
  SessionTranscriptLayout: typeof DefaultSessionTranscriptLayout
  SessionFollowupComposer: typeof DefaultSessionFollowupComposer
  SessionRecoveryActions: typeof DefaultSessionRecoveryActions
  ContextHealthBar: typeof DefaultContextHealthBar
  CompactionLineageLink?: unknown
}

const defaultComponents: SessionDetailShellComponents = {
  SessionTranscriptLayout: DefaultSessionTranscriptLayout,
  SessionFollowupComposer: DefaultSessionFollowupComposer,
  SessionRecoveryActions: DefaultSessionRecoveryActions,
  ContextHealthBar: DefaultContextHealthBar,
}

interface SessionErrorsEvidenceProps {
  failureCategory: string | null | undefined
  toolErrorCount: number | null | undefined
  failureReason: string | null | undefined
  failedTools: DisplayToolPart[]
  locate: (target: { toolCallId?: string; groupId?: string }) => void
}
const ERROR_SURFACE_CLASS = 'bg-danger-subtle text-danger border-danger-border'

const FAILURE_CATEGORY_LABELS: Record<string, string> = {
  cancelled: 'Cancelled',
  compaction: 'Context compaction',
  context_limit: 'Context limit',
  timeout: 'Timed out',
}

function formatFailureCategory(category: string): string {
  if (isExecutionUnavailableCategory(category)) return 'Execution unavailable'
  return FAILURE_CATEGORY_LABELS[category.toLowerCase()] ?? 'Execution failure'
}

function isExecutionUnavailableCategory(category: string | null | undefined): boolean {
  const normalized = category?.toLowerCase() ?? ''
  return normalized === 'runtime-unavailable'
    || normalized === 'execution-unavailable'
    || normalized === 'external-agent-unavailable'
}

export function SessionErrorsEvidence({
  failureCategory,
  toolErrorCount,
  failureReason,
  failedTools,
  locate,
}: SessionErrorsEvidenceProps) {
  const hasFailureCategory = failureCategory != null && failureCategory !== ''
  const hasFailureReason = failureReason != null && failureReason !== ''
  const hasToolErrors = toolErrorCount != null && toolErrorCount > 0
  const hasFailureEvidence = hasFailureCategory || hasFailureReason
  const executionUnavailable = isExecutionUnavailableCategory(failureCategory)
  const [currentIndex, setCurrentIndex] = useState(0)
  if (!hasFailureEvidence && !hasToolErrors) return null

  const activate = () => {
    const tool = failedTools[currentIndex]
    if (tool) locate({ toolCallId: tool.toolCallId })
  }
  const next = () => {
    if (failedTools.length === 0) return
    const nextIndex = (currentIndex + 1) % failedTools.length
    setCurrentIndex(nextIndex)
    const tool = failedTools[nextIndex]
    locate({ toolCallId: tool.toolCallId })
  }

  return (
    <div
      data-testid="session-errors-region"
      data-failure-category={failureCategory ?? ''}
      data-tool-error-count={toolErrorCount != null ? String(toolErrorCount) : ''}
      className={`border-b border-border px-4 py-2 ${ERROR_SURFACE_CLASS}`}
      onClick={activate}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          activate()
        }
      }}
      tabIndex={failedTools.length > 0 ? 0 : undefined}
      role={failedTools.length > 0 ? 'button' : hasFailureEvidence ? 'status' : undefined}
      aria-live={failedTools.length > 0 ? undefined : 'polite'}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
        <span className="inline-flex items-center gap-1 font-semibold">
          <AlertTriangleIcon className="h-3.5 w-3.5" aria-hidden="true" />
          {executionUnavailable ? 'Execution unavailable' : hasFailureEvidence ? 'Execution failed' : 'Tool errors detected'}
        </span>
        {hasFailureCategory && (
          <span
            className="inline-flex items-center rounded-full border border-danger-border bg-danger-subtle text-danger px-2 py-0.5 text-[10px] font-semibold"
            data-testid="session-errors-region-category"
          >
            {formatFailureCategory(failureCategory)}
          </span>
        )}
        {hasToolErrors && (
          <span
            className="inline-flex items-center gap-1"
            data-testid="session-errors-region-tool-count"
          >
            <span className="text-danger font-medium">{toolErrorCount}</span>
            <span>tool {toolErrorCount === 1 ? 'error' : 'errors'}</span>
          </span>
        )}
        {failureReason && (
          <span className="text-danger truncate max-w-[300px]" title={failureReason} data-testid="session-errors-region-reason">
            {failureReason}
          </span>
        )}
        {executionUnavailable && (
          <span data-testid="session-errors-region-next-action" className="text-danger">
            Wait for the configured runtime or provider to recover, then retry the launch from the Agent.
          </span>
        )}
        {failedTools.length > 1 && (
          <Button type="button" variant="link" size="sm" data-testid="session-errors-region-next-error" onClick={(event) => { event.stopPropagation(); next() }} className="h-auto p-0 text-xs text-danger">
            Next error
          </Button>
        )}
      </div>
    </div>
  )
}

export function SessionDetailShell({
  data,
  components,
}: {
  data: SessionDataSourceResult
  components?: Partial<SessionDetailShellComponents>
}) {
  const {
    SessionTranscriptLayout,
    SessionFollowupComposer,
    SessionRecoveryActions,
    ContextHealthBar,
  } = { ...defaultComponents, ...components }
  const {
    sessionKey,
    meta,
    statusKind,
    isRunning,
    canFollowup = isRunning,
    displayTurns,
    sessionTurns: turns,
    siblingNav,
    siblingSidebar,
    backPath,
    backLabel,
    issueTitle,
    workflowContextPath,
    workflowContextLabel,
    newContentAvailable,
    scrollToBottom,
    setIsNearBottom,
    isThinking,
    isStreaming,
    transcriptVersion,
    isLoading,
    isError,
    notFound,
    sendFollowup,
    followupIsPending,
    followupStatus,
    contextWindowUsed,
    contextWindowSize,
    contextUsagePercent,
    healthStatus,
    hasRecoveryActions,
    recoveryAvailable,
    recoverySessionName,
    recoverySessionId,
    handleRecoverySuccess,
    issueNumber,
    cancel,
    stop,
    emptyStateKind,
    launchObservation,
    projectId,
    supportsInputAttachments = false,
  } = data

  // ── All hooks must be before any early return ──
  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const headerRef = useRef<HTMLDivElement>(null)
  const isNearBottomRef = useRef(true)
  const { locate } = useTranscriptLocate({ scrollContainerRef })
  const resolveInputAttachment = useCallback((inputId: string, attachmentId: string): MarkdownAttachment | null => {
    if (!supportsInputAttachments || !projectId) return null
    const input = meta?.inputs?.find((candidate) => candidate.id === inputId)
    const attachment = input?.attachments?.find((candidate) => candidate.id === attachmentId)
    if (!attachment) return null
    return {
      url: `/api${agentInputAttachmentContentPath(projectId, sessionKey, inputId, attachmentId)}`,
      contentType: attachment.contentType || 'application/octet-stream',
      fileName: attachment.name,
      size: attachment.size,
    }
  }, [meta?.inputs, projectId, sessionKey, supportsInputAttachments])
  const failedTools = selectFailedToolCalls(displayTurns)
  const failedGroupIds = selectToolCallGroupIds(displayTurns)
  const isUserScrollingRef = useRef(false)
  const isSelectingTextRef = useRef(false)
  const initializedAutoScrollSessionRef = useRef<string | null>(null)
  const [queuedFollowup, setQueuedFollowup] = useState<{
    sessionKey: string
    transcriptVersion: number
  } | null>(null)

  const displayTurnCount = meta?.turnCount ?? turns.length
  const hasContextUsage = contextWindowUsed != null || contextWindowSize != null

  const recoveryBarContent = hasRecoveryActions ? (
    <div className="flex flex-col gap-2">
      <div className="flex flex-row flex-wrap gap-2 items-start justify-between md:flex-nowrap">
        {hasContextUsage && (
          <div className="flex-1 min-w-0">
            <ContextHealthBar
              contextWindowUsed={contextWindowUsed}
              contextWindowSize={contextWindowSize}
              contextUsagePercent={contextUsagePercent}
              healthStatus={healthStatus}
            />
          </div>
        )}
        {(recoverySessionName || recoverySessionId) && (
          <div className="contents md:block md:shrink-0">
            <SessionRecoveryActions
              issueNumber={issueNumber}
              sessionName={recoverySessionName ?? ''}
              genericSessionId={recoverySessionId ?? undefined}
              activity={meta?.activity}
              recoveryAvailable={recoveryAvailable}
              onSuccess={handleRecoverySuccess}
              bare

            />
          </div>
        )}
      </div>
    </div>
  ) : null

  const observationGuidance = launchObservation
    ? getAgentLaunchObservationMeaning(launchObservation) === 'reconcile'
      ? 'Launch outcome is unresolved. Re-read this observation or retry with the original Idempotency-Key.'
      : getAgentLaunchObservationMeaning(launchObservation) === 'result'
        ? 'Initial launch is terminal. Read the result and transcript; this Session remains available.'
        : 'Initial launch is accepted and still progressing. Continue observing the Job and transcript.'
    : null

  // Errors evidence (region between usage summary and transcript)
  const errorsEvidence = (
    <SessionErrorsEvidence
      failureCategory={meta?.eventSummary?.failureCategory ?? null}
      toolErrorCount={meta?.eventSummary?.toolErrorCount ?? null}
      failureReason={meta?.failureReason ?? null}
      failedTools={failedTools}
      locate={(target) => locate({ ...target, groupId: target.toolCallId ? failedGroupIds.get(target.toolCallId) : undefined })}
    />
  )

  // Scroll behavior hooks
  const handleScroll = useCallback(
    (evt?: Event) => {
      const container = scrollContainerRef.current
      if (!container) return

      const target = evt?.target as HTMLElement | null
      if (target && (target as HTMLElement).closest('[data-scrollable]')) return

      const distanceFromBottom = container.scrollHeight - container.scrollTop - container.clientHeight
      const threshold = 200
      const wasNearBottom = isNearBottomRef.current
      isNearBottomRef.current = distanceFromBottom < threshold

      if (!wasNearBottom && isNearBottomRef.current) {
        isUserScrollingRef.current = false
      } else if (wasNearBottom && !isNearBottomRef.current) {
        isUserScrollingRef.current = true
      }

      setIsNearBottom(isNearBottomRef.current)
    },
    [setIsNearBottom],
  )

  const handleScrollToBottom = useCallback(() => {
    const container = scrollContainerRef.current
    if (!container) return
    container.scrollTo({ top: container.scrollHeight, behavior: 'smooth' })
    scrollToBottom()
    isUserScrollingRef.current = false
  }, [scrollToBottom])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    let animationFrame: number | null = null
    const onScroll = (evt: Event) => {
      if (animationFrame !== null) cancelAnimationFrame(animationFrame)
      animationFrame = requestAnimationFrame(() => handleScroll(evt))
    }
    container.addEventListener('scroll', onScroll, { passive: true })
    return () => {
      container.removeEventListener('scroll', onScroll)
      if (animationFrame !== null) cancelAnimationFrame(animationFrame)
    }
  }, [handleScroll])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    const onSelectionChange = () => {
      const selection = window.getSelection()
      isSelectingTextRef.current = selection !== null && selection.toString().length > 0
    }
    document.addEventListener('selectionchange', onSelectionChange)
    return () => document.removeEventListener('selectionchange', onSelectionChange)
  }, [])

  useEffect(() => {
    if (!isRunning) {
      initializedAutoScrollSessionRef.current = null
      return
    }
    const container = scrollContainerRef.current
    if (!container) return

    if (initializedAutoScrollSessionRef.current !== sessionKey) {
      if (turns.length === 0) return
      initializedAutoScrollSessionRef.current = sessionKey
      handleScroll()
      return
    }

    if (!isNearBottomRef.current || isUserScrollingRef.current || isSelectingTextRef.current) return
    container.scrollTop = container.scrollHeight
  }, [handleScroll, isRunning, meta?.activity, sessionKey, transcriptVersion, turns.length])

  useEffect(() => {
    if (queuedFollowup?.sessionKey === sessionKey && transcriptVersion > queuedFollowup.transcriptVersion) {
      setQueuedFollowup(null)
    }
  }, [queuedFollowup, sessionKey, transcriptVersion])

  const handleFollowupSend = useCallback(async (text: string, attachmentIds: string[] = []) => {
    setQueuedFollowup({ sessionKey, transcriptVersion })
    try {
      return await sendFollowup(text, attachmentIds)
    } catch (error) {
      setQueuedFollowup((current) => current?.sessionKey === sessionKey ? null : current)
      throw error
    }
  }, [sendFollowup, sessionKey, transcriptVersion])

  useEffect(() => {
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    const handleResize = () => {
      if (
        initializedAutoScrollSessionRef.current === sessionKey
        && isNearBottomRef.current
        && !isUserScrollingRef.current
        && !isSelectingTextRef.current
      ) {
        container.scrollTop = container.scrollHeight
      }
    }
    window.addEventListener('resize', handleResize)
    return () => window.removeEventListener('resize', handleResize)
  }, [isRunning, sessionKey])

  if (notFound) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-center space-y-3">
          <div className="text-muted-foreground text-lg">Session not found</div>
        </div>
      </div>
    )
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-muted-foreground">Loading session...</div>
      </div>
    )
  }

  if (isError || !meta) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-center space-y-3">
          <div className="text-danger text-lg">Failed to load session</div>
          <p className="text-muted-foreground text-sm">An error occurred while fetching session data.</p>
        </div>
      </div>
    )
  }

  const header = (
    <SessionHeader
      backPath={backPath}
      backLabel={backLabel}
      issueTitle={issueTitle}
      workflowContextPath={workflowContextPath}
      workflowContextLabel={workflowContextLabel}
      meta={meta}
      statusKind={statusKind}
      turnCount={displayTurnCount}
      siblingNav={siblingNav}
      cancel={cancel}
      stop={stop}
      headerRef={headerRef}
    />
  )

  const hasTurns = turns.length > 0
  const showFollowupComposer = hasTurns || canFollowup || !isRunning

  return (
    <div className="flex flex-col flex-1 min-h-0 relative xl:flex-row">
      <div className="flex flex-col flex-1 min-h-0">
        <div
          ref={scrollContainerRef}
          className="flex-1 overflow-y-auto min-w-0 min-h-[120px] md:min-h-0"
          data-testid="session-transcript-scroll-container"
        >
          {header}
          <ScrollEngagedStickyTitle
            headerRef={headerRef}
            scrollContainerRef={scrollContainerRef}
            meta={meta}
            statusKind={statusKind}
            turnCount={displayTurnCount}
          />
          <SessionUsageSummary usage={meta.usage} />
           {errorsEvidence}
           {observationGuidance && (
             <div className="border-b border-border px-4 py-2 text-xs text-muted-foreground" data-testid="launch-observation-guidance">
               {observationGuidance}
             </div>
           )}
           <SessionInputTurnEvidence inputs={meta.inputs} turns={meta.turns} />
           {recoveryBarContent && (
             <div
               data-testid="session-recovery-bar"
               data-sticky="true"
               className="sticky top-9 z-20 border-b border-border bg-background px-4 py-2 md:py-3"
             >
               {recoveryBarContent}
             </div>
           )}

          {hasTurns ? (
            <SessionTranscriptLayout
              turns={displayTurns}
              inputIdsByTurn={meta?.turns?.map((turn) => turn.inputIds)}
              resolveAttachment={resolveInputAttachment}
              isRunning={isRunning}
              isThinking={isThinking}
              isStreaming={isStreaming}
              scrollContainerRef={scrollContainerRef}
            />
          ) : (
            <SessionEmptyStateView kind={emptyStateKind ?? 'unknown-no-content'} />
          )}
        </div>

        {showFollowupComposer && (
          <div data-testid="session-followup-composer-region">
              <SessionFollowupComposer
                onSend={handleFollowupSend}
                projectId={projectId}
                allowAttachments={supportsInputAttachments}
                isSending={followupIsPending}
                disabled={!canFollowup}
                hasQueuedFollowup={queuedFollowup?.sessionKey === sessionKey}
                followupStatus={followupStatus}
                className="py-0.5"
              />
          </div>
        )}

        {hasTurns && newContentAvailable && <JumpToBottomButton onClick={handleScrollToBottom} />}
      </div>
      {siblingSidebar}
    </div>
  )
}

// ── Sub-components ──

function getStageLabel(stage: string | null): string {
  if (!stage) return 'Session'
  return stage.charAt(0).toUpperCase() + stage.slice(1)
}

function sessionTimeAnchorMs(
  meta: import('../../../entities/coder-session').SessionMetadata,
): number | null {
  return meta.lastActivityAt ? new Date(meta.lastActivityAt).getTime() : null
}

const sessionStatusPresentation: Partial<Record<StatusKind, { label: string; className: string; dotClassName?: string; withDot?: boolean }>> = {
  active: {
    label: 'Active',
    className: 'bg-info-subtle text-info border-info-border',
    dotClassName: 'bg-info',
    withDot: true,
  },
  idle: {
    label: 'Idle',
    className: 'bg-muted text-muted-foreground border-border',
    dotClassName: 'bg-muted-foreground/60',
  },
  unknown: {
    label: 'Unknown',
    className: 'bg-warning-subtle text-warning border-warning-border',
    dotClassName: 'bg-warning',
  },
}

const stageChipPresentation: Record<string, string> = {
  plan: 'bg-info-subtle text-info border-info-border',
  review: 'bg-success-subtle text-success border-success-border',
  check: 'bg-warning-subtle text-warning border-warning-border',
  integrate: 'bg-muted text-muted-foreground border-border',
}

function StatusBadge({ kind }: { kind: StatusKind }) {
  const presentation = sessionStatusPresentation[kind] ?? sessionStatusPresentation.unknown!
  const { label, className, dotClassName, withDot } = presentation
  return (
    <span
      data-testid="session-status-badge"
      data-status-kind={kind}
      data-tone={className.startsWith('bg-danger') ? 'danger'
        : className.startsWith('bg-warning') ? 'warning'
        : className.startsWith('bg-success') ? 'success'
        : className.startsWith('bg-info') ? 'info'
        : 'neutral'}
      className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium border ${className}`}
    >
      {withDot && dotClassName && (
        <span className="relative flex h-2 w-2">
          <span className={`animate-ping absolute inline-flex h-full w-full rounded-full opacity-75 ${dotClassName}`} />
          <span className={`relative inline-flex rounded-full h-2 w-2 ${dotClassName}`} />
        </span>
      )}
      {label}
    </span>
  )
}

function SessionHeader({
  backPath,
  backLabel,
  issueTitle,
  workflowContextPath,
  workflowContextLabel,
  meta,
  statusKind,
  turnCount,
  siblingNav,
  cancel,
  stop,
  headerRef,
}: {
  backPath: string
  backLabel: string
  issueTitle?: string
  workflowContextPath?: string
  workflowContextLabel?: string
  meta: import('../../../entities/coder-session').SessionMetadata
  statusKind: StatusKind
  turnCount: number
  siblingNav?: React.ReactNode
  cancel: SessionDataSourceResult['cancel']
  stop: SessionDataSourceResult['stop']
  headerRef: RefObject<HTMLDivElement | null>
}) {
  const [cancelDialogOpen, setCancelDialogOpen] = useState(false)
  const [controlOperation, setControlOperation] = useState<'cancel' | 'stop'>('cancel')
  const [cancelState, setCancelState] = useState<string | null>(null)
  const showCancelControl = cancel != null
  const showStopControl = stop != null
  const activeControl = controlOperation === 'cancel' ? cancel : stop
  const activeControlIsPending = activeControl?.isPending ?? false

  const changedFiles = meta?.changedFiles
  const fileSummary = changedFiles && changedFiles.length > 0
    ? changedFiles.length === 1 ? '1 file changed' : `${changedFiles.length} files changed`
    : null

  const eventSummary = meta?.eventSummary

  const stageLower = (meta?.stage ?? '').toLowerCase()
  const stageClassName = stageChipPresentation[stageLower] ?? 'bg-muted text-muted-foreground border-border'

  const isWideViewport = useMediaQuery('(min-width: 1280px)')

  const lastActivityAnchorMs = sessionTimeAnchorMs(meta)
  const lastActivityTime = lastActivityAnchorMs == null ? null : formatSessionTime({
    date: lastActivityAnchorMs,
    statusKind,
    now: Date.now(),
  })

  return (
    <div
      ref={headerRef}
      data-testid="session-header"
      className="border-b border-border bg-background px-4 py-2 md:py-3 shrink-0 min-w-0"
    >
      <div className="flex flex-wrap items-center gap-2 text-sm mb-2 min-w-0">
        <Link
          to={backPath}
          className="flex items-center gap-1 text-info hover:text-info/80 transition-colors whitespace-nowrap shrink-0"
          data-testid="session-back-link"
        >
          <ChevronLeftIcon className="h-4 w-4 shrink-0" />
          <span>{backLabel}</span>
        </Link>
        {issueTitle && (
          <>
            <span className="hidden md:inline text-muted-foreground/40 shrink-0">/</span>
            <span className="hidden md:inline text-muted-foreground truncate min-w-0">{issueTitle}</span>
          </>
        )}
        {workflowContextPath && workflowContextLabel && (
          <Link
            to={workflowContextPath}
            data-testid="session-workflow-context-link"
            className="inline-flex items-center gap-1 text-xs text-info hover:text-info/80 transition-colors shrink-0"
            title={workflowContextLabel}
          >
            <ChevronRightIcon className="h-3.5 w-3.5" aria-hidden="true" />
            <span>{workflowContextLabel}</span>
          </Link>
        )}
        {siblingNav && !isWideViewport && (
          <div className="ml-auto flex max-w-full min-w-0 flex-wrap items-center gap-1" data-testid="session-sibling-navigation-slot" data-viewport="narrow">
            {siblingNav}
          </div>
        )}
      </div>

      <div
        data-testid="session-source-context"
        className="mb-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground"
      >
        <span className="font-medium text-foreground">
          {meta.source === 'workflow' ? 'Workflow Session' : 'Agent Session'}
        </span>
        {meta.agentName && <span>Agent: {meta.agentName}</span>}
        {meta.sessionName && meta.source === 'workflow' && <span>Work: {meta.sessionName}</span>}
        {meta.workflowRunId && <span>Workflow run: {meta.workflowRunId}</span>}
      </div>

      <div
        data-testid="session-header-metadata-row"

        className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground min-w-0"
      >
        <h1
          data-testid="session-header-name"
          className="text-lg font-semibold text-foreground truncate"
        >
          {meta.sessionName ?? 'Session'}
        </h1>
        <StatusBadge kind={statusKind} />
        <span
          data-testid="session-header-stage"
          data-stage={meta?.stage ?? ''}
          className={`px-2 py-0.5 rounded-full border font-medium ${stageClassName}`}
        >
          {getStageLabel(meta?.stage ?? null)}
        </span>

        {meta?.model && eventSummary?.resolvedModel && meta.model !== eventSummary.resolvedModel ? (
          <span
            data-testid="session-header-model"
            data-model={meta.model}
            className="text-muted-foreground"
          >
            {meta.model} <span className="text-muted-foreground/40">→</span>{' '}
            <span className="text-info">{eventSummary.resolvedModel}</span>
          </span>
        ) : meta?.model ? (
          <span
            data-testid="session-header-model"
            data-model={meta.model}
          >
            {meta.model}
          </span>
        ) : null}

        <span
          data-testid="session-header-turn-count"
          data-turn-count={turnCount}
        >
          {turnCount} turn{turnCount !== 1 ? 's' : ''}
        </span>

        {lastActivityTime && (
          <span
            data-testid="session-header-last-activity"
            data-last-activity={lastActivityTime.primary}
            title={lastActivityTime.secondary}
          >
            {lastActivityTime.primary}
          </span>
        )}
        {fileSummary && (
          <span data-testid="session-header-file-summary">{fileSummary}</span>
        )}
        {meta?.sessionId && (
          <SessionIdCopyButton
            sessionId={meta.sessionId}
            truncated={meta.sessionId.slice(0, 8)}
          />
        )}
      </div>

      {(showCancelControl || showStopControl) && (
        <div
          data-testid="session-header-secondary-actions"
          className="mt-1 flex justify-end gap-1"
        >
          {showCancelControl && (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => { setControlOperation('cancel'); setCancelDialogOpen(true) }}
              data-testid="session-cancel-trigger"
              data-control-operation="cancel"
              data-turn-state="queued"
              aria-label="Cancel Turn"
              type="button"
            >
              <CircleStopIcon className="h-3.5 w-3.5" aria-hidden="true" />
              Cancel Turn
            </Button>
          )}
          {showStopControl && (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => { setControlOperation('stop'); setCancelDialogOpen(true) }}
              data-testid="session-stop-trigger"
              data-control-operation="stop"
              data-turn-state="executing"
              aria-label="Stop Turn"
              type="button"
            >
              <CircleStopIcon className="h-3.5 w-3.5" aria-hidden="true" />
              Stop Turn
            </Button>
          )}
        </div>
      )}

      {(showCancelControl || showStopControl) && activeControl && (
        <AlertDialog
          open={cancelDialogOpen}
          onOpenChange={(open) => {
            if (activeControlIsPending) return
            setCancelDialogOpen(open)
          }}
          title={controlOperation === 'cancel' ? 'Cancel this Turn?' : 'Stop this Turn?'}
          description={controlOperation === 'cancel'
            ? 'This deterministically cancels a queued Turn.'
            : 'This requests that the Runtime stop an executing Turn; the result may be unknown.'}
          confirmLabel={controlOperation === 'cancel' ? 'Cancel Turn' : 'Stop Turn'}
          cancelLabel="Keep running"
          tone="destructive"
          loading={activeControlIsPending}
          onConfirm={() => {
            activeControl.mutate({
              onSuccess: (result) => {
                setCancelState(result.state)
                setCancelDialogOpen(false)
              },
            })
          }}
          data-testid="session-cancel-alert"
          data-control-operation={controlOperation}
        />
      )}
      {cancelState && (
        <div className="px-4 pt-2 text-xs text-muted-foreground" role="status" data-testid="session-cancel-result">
          Turn result: {cancelState}
          {cancelState === 'unknown' && <span> Verification: Session view</span>}
          {cancelState === 'stop-requested' && <span> Verification: Session view (Runtime will report terminal result)</span>}
        </div>
      )}

    </div>
  )
}

function ScrollEngagedStickyTitle({
  headerRef,
  scrollContainerRef,
  meta,
  statusKind,
  turnCount,
}: {
  headerRef: RefObject<HTMLDivElement | null>
  scrollContainerRef: RefObject<HTMLDivElement | null>
  meta: import('../../../entities/coder-session').SessionMetadata
  statusKind: StatusKind
  turnCount: number
}) {
  const [engaged, setEngaged] = useState(false)

  useEffect(() => {
    const header = headerRef.current
    const scrollContainer = scrollContainerRef.current
    if (!header || !scrollContainer || typeof IntersectionObserver === 'undefined') return

    const observer = new IntersectionObserver(([entry]) => {
      if (entry) setEngaged(entry.intersectionRatio < 0.001)
    }, {
      root: scrollContainer,
      threshold: 0,
    })
    observer.observe(header)
    return () => observer.disconnect()
  }, [headerRef, scrollContainerRef])

  if (!engaged) return null

  return (
    <StickySessionTitle
      meta={meta}
      statusKind={statusKind}
      turnCount={turnCount}
    />
  )
}

function StickySessionTitle({ meta, statusKind, turnCount }: {
  meta: import('../../../entities/coder-session').SessionMetadata
  statusKind: StatusKind
  turnCount: number
}) {
  return (
    <div className="sticky top-0 z-20 border-b border-border bg-background px-4 py-2" data-testid="session-sticky-title">
      <div className="flex items-center gap-2 text-sm">
        <span className="font-medium truncate">{meta?.sessionName ?? 'Session'}</span>
        <StatusBadge kind={statusKind} />
        <span className="text-muted-foreground text-xs">{turnCount} turn{turnCount !== 1 ? 's' : ''}</span>
      </div>
    </div>
  )
}

const SESSION_ID_COPY_RESET_MS = 1500

function SessionIdCopyButton({ sessionId, truncated }: { sessionId: string; truncated: string }) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle')
  const [tooltipPinnedOpen, setTooltipPinnedOpen] = useState(false)

  useEffect(() => {
    if (copyState === 'idle' && !tooltipPinnedOpen) return
    const timer = window.setTimeout(() => {
      setCopyState('idle')
      setTooltipPinnedOpen(false)
    }, SESSION_ID_COPY_RESET_MS)
    return () => window.clearTimeout(timer)
  }, [copyState, tooltipPinnedOpen])

  const handleClick = () => {
    const clipboard = typeof navigator !== 'undefined' ? navigator.clipboard : undefined
    if (!clipboard?.writeText) {
      setCopyState('failed')
      setTooltipPinnedOpen(true)
      return
    }
    clipboard.writeText(sessionId).then(
      () => {
        setCopyState('copied')
        setTooltipPinnedOpen(false)
      },
      () => {
        setCopyState('failed')
        setTooltipPinnedOpen(true)
      },
    )
  }

  const showTooltip = tooltipPinnedOpen
  const ariaLabel = copyState === 'copied'
    ? 'Copied!'
    : `Copy session id ${sessionId}`
  const visibleLabel = copyState === 'copied'
    ? 'Copied!'
    : copyState === 'failed'
      ? 'Copy unavailable'
      : truncated

  return (
    <span className="relative inline-flex items-center">
      <button
        type="button"
        data-testid="session-header-session-id"
        data-session-id={sessionId}
        data-copy-state={copyState}
        data-tooltip-pinned={showTooltip ? 'true' : 'false'}
        aria-label={ariaLabel}
        title={sessionId}
        onClick={handleClick}
        className={`inline-flex items-center gap-1 rounded font-mono text-xs ${
          copyState === 'copied'
            ? 'text-success'
            : copyState === 'failed'
              ? 'text-danger'
              : 'text-muted-foreground hover:text-foreground'
        }`}
      >
        <span>{visibleLabel}</span>
        {copyState === 'copied' ? (
          <CheckIcon className="h-3 w-3" aria-hidden="true" />
        ) : (
          <CopyIcon className="h-3 w-3" aria-hidden="true" />
        )}
      </button>
      {showTooltip && (
        <span
          role="tooltip"
          data-testid="session-header-session-id-tooltip"
          className="absolute bottom-full left-0 z-50 mb-1 max-w-[420px] break-all rounded-md border bg-popover px-2 py-1 font-mono text-[11px] text-popover-foreground shadow-md"
        >
          {sessionId}
        </span>
      )}
    </span>
  )
}

function SessionInputTurnEvidence({
  inputs,
  turns,
}: {
  inputs?: SessionInputObservation[] | null
  turns?: AgentTurnObservation[] | null
}) {
  if (!inputs?.length && !turns?.length) return null
  const inputById = new Map((inputs ?? []).map((input) => [input.id, input]))
  const groupedInputIds = new Set((turns ?? []).flatMap((turn) => turn.inputIds))
  const orderedInputs = [...(inputs ?? [])].sort((a, b) => a.sequence - b.sequence)

  return (
    <section
      data-testid="session-input-turn-evidence"
      className="border-b border-border bg-muted/20 px-4 py-3"
      aria-label="Session inputs and turns"
    >
      <div className="mb-2 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">Inputs and turns</div>
      <div className="space-y-2">
        {(turns ?? []).map((turn) => (
          <div key={turn.id} data-testid={`session-turn-evidence-${turn.id}`} className="rounded border border-border bg-background px-3 py-2">
            <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
              <span className="font-medium text-foreground">Turn {turn.sequence + 1}</span>
              <span className="font-mono text-muted-foreground">{turn.id}</span>
              <span data-testid={`session-turn-status-${turn.id}`} className="rounded-full border border-border px-1.5 py-0.5 text-[10px]">{turn.status}</span>
            </div>
            <div className="mt-2 space-y-1">
              {turn.inputIds.map((inputId) => {
                const input = inputById.get(inputId)
                return (
                  <div key={inputId} data-testid={`session-input-evidence-${inputId}`} className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground">
                    <span>Input {input?.sequence ?? '?'}</span>
                    <span className="font-mono">{inputId}</span>
                    <span data-testid={`session-input-acceptance-${inputId}`}>accepted: {input?.acceptance ?? 'unknown'}</span>
                    <span data-testid={`session-input-delivery-${inputId}`}>delivered: {turn.status}</span>
                  </div>
                )
              })}
            </div>
          </div>
        ))}
        {orderedInputs.filter((input) => !groupedInputIds.has(input.id)).map((input) => (
          <div key={input.id} data-testid={`session-input-evidence-${input.id}`} className="flex flex-wrap items-center gap-x-2 gap-y-1 rounded border border-border bg-background px-3 py-2 text-xs text-muted-foreground">
            <span>Input {input.sequence}</span>
            <span className="font-mono">{input.id}</span>
            <span data-testid={`session-input-acceptance-${input.id}`}>accepted: {input.acceptance}</span>
            <span data-testid={`session-input-delivery-${input.id}`}>delivered: unknown</span>
          </div>
        ))}
      </div>
    </section>
  )
}

function SessionEmptyStateView({ kind }: { kind: EmptyStateKind }) {
  if (kind === 'active-no-content') {
    return (
      <div className="flex items-center justify-center flex-1" data-testid="session-empty-state" data-state-kind={kind}>
        <div className="text-center space-y-3">
          <div className="text-info text-lg">The session is active but no content has been received</div>
          <p className="text-muted-foreground text-sm">Waiting for runtime output.</p>
        </div>
      </div>
    )
  }

  if (kind === 'unknown-no-content') {
    return (
      <div className="flex items-center justify-center flex-1" data-testid="session-empty-state" data-state-kind={kind}>
        <div className="text-center space-y-3">
          <div className="text-warning text-lg">Session activity is unknown</div>
          <p className="text-muted-foreground text-sm">Mohist cannot confirm whether execution is still active.</p>
        </div>
      </div>
    )
  }

  return (
    <div className="flex items-center justify-center flex-1" data-testid="session-empty-state" data-state-kind={kind}>
      <div className="text-center space-y-3">
        <div className="text-muted-foreground text-lg">This session is idle</div>
        <p className="text-muted-foreground text-sm">Send a follow-up to continue the conversation.</p>
      </div>
    </div>
  )
}

function JumpToBottomButton({ onClick }: { onClick: () => void }) {
  return (
    <Button
      onClick={onClick}
      className="absolute bottom-4 right-4 rounded-full bg-foreground text-xs text-background shadow-lg hover:bg-foreground/90"
    >
      <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 17a.75.75 0 01-.75-.75V5.612L5.29 9.77a.75.75 0 01-1.08-1.04l5.25-5.5a.75.75 0 011.08 0l5.25 5.5a.75.75 0 11-1.08 1.04l-3.96-4.158V16.25A.75.75 0 0110 17z" clipRule="evenodd" />
      </svg>
      Jump to bottom
    </Button>
  )
}
