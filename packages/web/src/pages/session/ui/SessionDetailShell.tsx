import { useEffect, useLayoutEffect, useRef, useState, useCallback, type RefObject } from 'react'
import { Link } from 'react-router-dom'
import { ChevronLeftIcon, ChevronRightIcon, CircleStopIcon, CheckIcon, CopyIcon } from 'lucide-react'
import { SessionTranscriptLayout as DefaultSessionTranscriptLayout } from '../../../widgets/session-transcript'
import {
  SessionFollowupComposer as DefaultSessionFollowupComposer,
  SessionRecoveryActions as DefaultSessionRecoveryActions,
} from '../../../widgets/coder-session'
import { ContextHealthBar as DefaultContextHealthBar } from '../../../widgets/coder-session'
import { Button } from '@/shared/ui/components/button'
import { AlertDialog } from '@/shared/ui/components/alert-dialog'
import { formatSessionTime } from '@/shared/lib/format-time'
import { useMediaQuery } from '@/shared/lib/use-media-query'
import { getAgentLaunchObservationMeaning } from '../../../entities/agent'
import { useProjectPath } from '../../../entities/project'
import type { StatusKind, SessionDataSourceResult } from '../data/SessionDataSource'
import { SessionUsageSummary } from './SessionUsageSummary'
import { SessionErrorsEvidence } from './SessionErrorsEvidence'
import { TimelineViewToggle } from './TimelineViewToggle'
import { StatusBadge } from './StatusBadge'
import { getStageLabel, sessionTimeAnchorMs } from './sessionPresentation'
import type { AgentWorkInterruption } from '../../../entities/coder-session'

function SessionInterruptionBanner({ interruption }: { interruption: AgentWorkInterruption }) {
  return (
    <section
      className="border-b border-warning-border bg-warning-subtle px-4 py-2.5 text-sm text-warning"
      data-testid="session-interruption-banner"
      role="status"
    >
      <div className="font-semibold">Runner update interruption: {interruption.state}</div>
      <div className="mt-1 grid gap-x-3 gap-y-0.5 text-xs sm:grid-cols-[auto_1fr]">
        <span>Update</span>
        <span className="break-all font-mono">{interruption.updateOperationId}</span>
        <span>Work</span>
        <span className="break-all font-mono">{interruption.workId}</span>
        <span>Recovery generation</span>
        <span>{interruption.recoveryGeneration}</span>
        {interruption.originalTurnId && (
          <>
            <span>Original turn</span>
            <span className="break-all font-mono">{interruption.originalTurnId}</span>
          </>
        )}
        {interruption.replacementTurnId && (
          <>
            <span>Replacement turn</span>
            <span className="break-all font-mono">{interruption.replacementTurnId}</span>
          </>
        )}
      </div>
      <p className="mt-1 text-xs">{interruption.expectedRecoveryPath}</p>
      {interruption.stopFailure && <p className="mt-1 text-xs">{interruption.stopFailure}</p>}
    </section>
  )
}

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

export function SessionDetailShell({
  data,
  components,
}: {
  data: SessionDataSourceResult
  components?: Partial<SessionDetailShellComponents>
}) {
  const { SessionTranscriptLayout, SessionFollowupComposer, SessionRecoveryActions, ContextHealthBar } = {
    ...defaultComponents,
    ...components,
  }
  const {
    sessionKey,
    meta,
    statusKind,
    isRunning,
    canFollowup = isRunning,
    sessionTurns: turns,
    facts,
    items,
    entries,
    currentActivity,
    resolveTimelineReference,
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
    stop,
    launchObservation,
    projectId,
    supportsInputAttachments = false,
    transcriptView,
    setTranscriptView,
  } = data

  // ── All hooks must be before any early return ──
  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const headerRef = useRef<HTMLDivElement>(null)
  const isNearBottomRef = useRef(true)
  const [localTimelineView, setLocalTimelineView] = useState<'summary' | 'raw'>('summary')
  const timelineView: 'summary' | 'raw' = transcriptView === 'raw' ? 'raw' : localTimelineView
  const pendingTimelineAnchorRef = useRef<string | null>(null)
  const isUserScrollingRef = useRef(false)
  const isSelectingTextRef = useRef(false)
  const initializedAutoScrollSessionRef = useRef<string | null>(null)
  const [queuedFollowup, setQueuedFollowup] = useState<{
    sessionKey: string
    transcriptVersion: number
  } | null>(null)

  const displayTurnCount = meta?.turnCount ?? turns.length
  const hasContextUsage = contextWindowUsed != null || contextWindowSize != null

  const findVisibleTimelineSourceId = useCallback(() => {
    const container = scrollContainerRef.current
    if (!container) return null
    const containerRect = container.getBoundingClientRect()
    return (
      Array.from(container.querySelectorAll<HTMLElement>('[data-timeline-source-id]')).find((element) => {
        if (element.classList.contains('sr-only')) return false
        const rect = element.getBoundingClientRect()
        return rect.bottom > containerRect.top && rect.top < containerRect.bottom
      })?.dataset.timelineSourceId ?? null
    )
  }, [])

  const locateTimelineSource = useCallback((sourceId: string) => {
    const container = scrollContainerRef.current
    if (!container) return
    const target = Array.from(container.querySelectorAll<HTMLElement>('[data-timeline-source-id]')).find(
      (element) => element.dataset.timelineSourceId === sourceId,
    )
    target?.scrollIntoView({ block: 'nearest' })
  }, [])

  const changeTimelineView = useCallback(
    (nextView: 'summary' | 'raw') => {
      if (nextView === timelineView) return
      pendingTimelineAnchorRef.current = findVisibleTimelineSourceId()
      if (setTranscriptView) setTranscriptView(nextView === 'raw' ? 'raw' : 'public')
      else setLocalTimelineView(nextView)
    },
    [findVisibleTimelineSourceId, setTranscriptView, timelineView],
  )

  useLayoutEffect(() => {
    const sourceId = pendingTimelineAnchorRef.current
    if (!sourceId) return
    pendingTimelineAnchorRef.current = null
    locateTimelineSource(sourceId)
  }, [locateTimelineSource, timelineView])

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
              runtimeSessionId={meta?.runtimeSessionId}
              runtime={meta?.runtime}
              activity={meta?.activity}
              recoveryAvailable={recoveryAvailable}
              onSettled={handleRecoverySuccess}
              bare
            />
          </div>
        )}
      </div>
    </div>
  ) : null

  const observationIsRecovering = launchObservation?.jobStatus === 'recovering'
  const observationGuidance = launchObservation
    ? observationIsRecovering
      ? 'The AgentJob is recovering from an interrupted runner. The original work identity remains recoverable.'
      : getAgentLaunchObservationMeaning(launchObservation) === 'reconcile'
        ? 'Launch outcome is unresolved. Re-read this observation or retry with the original Idempotency-Key.'
        : getAgentLaunchObservationMeaning(launchObservation) === 'result'
          ? 'Initial launch is terminal. Read the result and transcript; this Session remains available.'
          : 'Initial launch is accepted and still progressing. Continue observing the Job and transcript.'
    : null

  // Errors evidence (region between usage summary and transcript)
  const errorsEvidence = meta?.interruption ? null : (
    <SessionErrorsEvidence
      failureCategory={meta?.eventSummary?.failureCategory ?? null}
      toolErrorCount={meta?.eventSummary?.toolErrorCount ?? null}
      failureReason={meta?.failureReason ?? null}
      failedItems={(items ?? []).filter((item) => item.renderClass === 'error')}
      locate={locateTimelineSource}
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

  const handleFollowupSend = useCallback(
    async (text: string, attachmentIds: string[] = []) => {
      setQueuedFollowup({ sessionKey, transcriptVersion })
      try {
        return await sendFollowup(text, attachmentIds)
      } catch (error) {
        setQueuedFollowup((current) => (current?.sessionKey === sessionKey ? null : current))
        throw error
      }
    },
    [sendFollowup, sessionKey, transcriptVersion],
  )

  useEffect(() => {
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    const handleResize = () => {
      if (
        initializedAutoScrollSessionRef.current === sessionKey &&
        isNearBottomRef.current &&
        !isUserScrollingRef.current &&
        !isSelectingTextRef.current
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
          {meta.interruption && <SessionInterruptionBanner interruption={meta.interruption} />}
          {errorsEvidence}
          {observationGuidance && (
            <div
              className="border-b border-border px-4 py-2 text-xs text-muted-foreground"
              data-testid="launch-observation-guidance"
            >
              {observationGuidance}
            </div>
          )}
          {observationIsRecovering && launchObservation && (
            <div
              data-testid="launch-observation-recovering"
              className="border-b border-warning-border bg-warning-subtle px-4 py-2 text-xs text-warning"
            >
              <div className="font-semibold">Recovering</div>
              {launchObservation.jobFailureReason && <div>Reason: {launchObservation.jobFailureReason}</div>}
              {launchObservation.recoveryDeadlineAt && (
                <div>Recovery deadline: {launchObservation.recoveryDeadlineAt}</div>
              )}
            </div>
          )}
          {recoveryBarContent && (
            <div
              data-testid="session-recovery-bar"
              data-sticky="true"
              className="sticky top-9 z-20 border-b border-border bg-background px-4 py-2 md:py-3"
            >
              {recoveryBarContent}
            </div>
          )}
          <TimelineViewToggle value={timelineView} onChange={changeTimelineView} />
          <SessionTranscriptLayout
            entries={entries}
            facts={facts}
            currentActivity={currentActivity}
            viewMode={timelineView}
            resolveReference={resolveTimelineReference}
          />
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

const stageChipPresentation: Record<string, string> = {
  plan: 'bg-info-subtle text-info border-info-border',
  review: 'bg-success-subtle text-success border-success-border',
  check: 'bg-warning-subtle text-warning border-warning-border',
  integrate: 'bg-muted text-muted-foreground border-border',
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
  stop: SessionDataSourceResult['stop']
  headerRef: RefObject<HTMLDivElement | null>
}) {
  const [stopDialogOpen, setStopDialogOpen] = useState(false)
  const [stopState, setStopState] = useState<string | null>(null)
  const showStopControl = stop != null
  const activeControlIsPending = stop?.isPending ?? false

  const changedFiles = meta?.changedFiles
  const fileSummary =
    changedFiles && changedFiles.length > 0
      ? changedFiles.length === 1
        ? '1 file changed'
        : `${changedFiles.length} files changed`
      : null

  const eventSummary = meta?.eventSummary

  const stageLower = (meta?.stage ?? '').toLowerCase()
  const stageClassName = stageChipPresentation[stageLower] ?? 'bg-muted text-muted-foreground border-border'

  const isWideViewport = useMediaQuery('(min-width: 1280px)')
  const toProjectPath = useProjectPath()

  const lastActivityAnchorMs = sessionTimeAnchorMs(meta)
  const lastActivityTime =
    lastActivityAnchorMs == null
      ? null
      : formatSessionTime({
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
          <div
            className="ml-auto flex max-w-full min-w-0 flex-wrap items-center gap-1"
            data-testid="session-sibling-navigation-slot"
            data-viewport="narrow"
          >
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
        {meta.agentName && meta.agentId ? (
          <Link
            to={toProjectPath(`/agents/${encodeURIComponent(meta.agentId)}`)}
            data-testid="session-agent-link"
            className="text-info hover:text-info/80 transition-colors"
          >
            Agent: {meta.agentName}
          </Link>
        ) : meta.agentName ? (
          <span>Agent: {meta.agentName}</span>
        ) : null}
        {meta.origin && <span data-testid="session-origin">Origin: {meta.origin}</span>}
        {meta.sessionName && meta.source === 'workflow' && <span>Work: {meta.sessionName}</span>}
        {meta.workflowRunId && <span>Workflow run: {meta.workflowRunId}</span>}
        {meta.workspace && (
          <Link
            to={toProjectPath(`/workspaces/${encodeURIComponent(meta.workspace)}`)}
            data-testid="session-workspace-link"
            className="text-info hover:text-info/80 transition-colors"
          >
            Workspace: {meta.workspace}
          </Link>
        )}
      </div>

      <div
        data-testid="session-header-metadata-row"
        className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground min-w-0"
      >
        <h1 data-testid="session-header-name" className="text-lg font-semibold text-foreground truncate">
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
          <span data-testid="session-header-model" data-model={meta.model} className="text-muted-foreground">
            {meta.model} <span className="text-muted-foreground/40">→</span>{' '}
            <span className="text-info">{eventSummary.resolvedModel}</span>
          </span>
        ) : meta?.model ? (
          <span data-testid="session-header-model" data-model={meta.model}>
            {meta.model}
          </span>
        ) : null}

        <span data-testid="session-header-turn-count" data-turn-count={turnCount}>
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
        {fileSummary && <span data-testid="session-header-file-summary">{fileSummary}</span>}
        {meta?.sessionId && <SessionIdCopyButton sessionId={meta.sessionId} truncated={meta.sessionId.slice(0, 8)} />}
      </div>

      {showStopControl && (
        <div data-testid="session-header-secondary-actions" className="mt-1 flex justify-end gap-1">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => setStopDialogOpen(true)}
            data-testid="session-stop-trigger"
            data-control-operation="stop"
            data-turn-state={stop?.state}
            aria-label="Stop Turn"
            type="button"
          >
            <CircleStopIcon className="h-3.5 w-3.5" aria-hidden="true" />
            Stop Turn
          </Button>
        </div>
      )}

      {showStopControl && stop && (
        <AlertDialog
          open={stopDialogOpen}
          onOpenChange={(open) => {
            if (activeControlIsPending) return
            setStopDialogOpen(open)
          }}
          title="Stop this Turn?"
          description={
            stop.state === 'queued'
              ? 'This records the queued Turn as cancelled.'
              : 'This requests that the Runtime stop the executing Turn; the result may be unknown.'
          }
          confirmLabel="Stop Turn"
          cancelLabel="Keep running"
          tone="destructive"
          loading={activeControlIsPending}
          onConfirm={() => {
            stop.mutate({
              onSuccess: (result) => {
                setStopState(result.state)
                setStopDialogOpen(false)
              },
            })
          }}
          data-testid="session-stop-alert"
          data-control-operation="stop"
        />
      )}
      {stopState && (
        <div className="px-4 pt-2 text-xs text-muted-foreground" role="status" data-testid="session-stop-result">
          Turn result: {stopState}
          {stopState === 'unknown' && <span> Verification: Session view</span>}
          {stopState === 'stop-requested' && (
            <span> Verification: Session view (Runtime will report terminal result)</span>
          )}
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

    const observer = new IntersectionObserver(
      ([entry]) => {
        if (entry) setEngaged(entry.intersectionRatio < 0.001)
      },
      {
        root: scrollContainer,
        threshold: 0,
      },
    )
    observer.observe(header)
    return () => observer.disconnect()
  }, [headerRef, scrollContainerRef])

  if (!engaged) return null

  return <StickySessionTitle meta={meta} statusKind={statusKind} turnCount={turnCount} />
}

function StickySessionTitle({
  meta,
  statusKind,
  turnCount,
}: {
  meta: import('../../../entities/coder-session').SessionMetadata
  statusKind: StatusKind
  turnCount: number
}) {
  return (
    <div
      className="sticky top-0 z-20 border-b border-border bg-background px-4 py-2"
      data-testid="session-sticky-title"
    >
      <div className="flex items-center gap-2 text-sm">
        <span className="font-medium truncate">{meta?.sessionName ?? 'Session'}</span>
        <StatusBadge kind={statusKind} />
        <span className="text-muted-foreground text-xs">
          {turnCount} turn{turnCount !== 1 ? 's' : ''}
        </span>
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
  const ariaLabel = copyState === 'copied' ? 'Copied!' : `Copy session id ${sessionId}`
  const visibleLabel = copyState === 'copied' ? 'Copied!' : copyState === 'failed' ? 'Copy unavailable' : truncated

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

function JumpToBottomButton({ onClick }: { onClick: () => void }) {
  return (
    <Button
      onClick={onClick}
      className="absolute bottom-4 right-4 rounded-full bg-foreground text-xs text-background shadow-lg hover:bg-foreground/90"
    >
      <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
        <path
          fillRule="evenodd"
          d="M10 17a.75.75 0 01-.75-.75V5.612L5.29 9.77a.75.75 0 01-1.08-1.04l5.25-5.5a.75.75 0 011.08 0l5.25 5.5a.75.75 0 11-1.08 1.04l-3.96-4.158V16.25A.75.75 0 0110 17z"
          clipRule="evenodd"
        />
      </svg>
      Jump to bottom
    </Button>
  )
}
