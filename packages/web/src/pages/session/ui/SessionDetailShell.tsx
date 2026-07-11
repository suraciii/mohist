import { useEffect, useRef, useState, useCallback } from 'react'
import { Link } from 'react-router-dom'
import { ChevronLeftIcon, CircleStopIcon } from 'lucide-react'
import { SessionTranscriptLayout as DefaultSessionTranscriptLayout } from '../../../widgets/session-transcript'
import { SessionFollowupComposer as DefaultSessionFollowupComposer, SessionRecoveryActions as DefaultSessionRecoveryActions } from '../../../widgets/coder-session'
import { ContextHealthBar as DefaultContextHealthBar, CompactionLineageLink as DefaultCompactionLineageLink } from '../../../widgets/session-health'
import { Button } from '@/shared/ui/components/button'
import { AlertDialog } from '@/shared/ui/components/alert-dialog'
import { formatCompact, formatCost } from '../../../shared/lib/format-compact'
import type { StatusKind, SessionDataSourceResult } from '../data/SessionDataSource'
import { SessionUsageSummary } from './SessionUsageSummary'

export interface SessionDetailShellComponents {
  SessionTranscriptLayout: typeof DefaultSessionTranscriptLayout
  SessionFollowupComposer: typeof DefaultSessionFollowupComposer
  SessionRecoveryActions: typeof DefaultSessionRecoveryActions
  ContextHealthBar: typeof DefaultContextHealthBar
  CompactionLineageLink: typeof DefaultCompactionLineageLink
}

const defaultComponents: SessionDetailShellComponents = {
  SessionTranscriptLayout: DefaultSessionTranscriptLayout,
  SessionFollowupComposer: DefaultSessionFollowupComposer,
  SessionRecoveryActions: DefaultSessionRecoveryActions,
  ContextHealthBar: DefaultContextHealthBar,
  CompactionLineageLink: DefaultCompactionLineageLink,
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
    CompactionLineageLink,
  } = { ...defaultComponents, ...components }
  const {
    meta,
    statusKind,
    isRunning,
    displayTurns,
    sessionTurns: turns,
    siblingNav,
    siblingSidebar,
    backPath,
    backLabel,
    issueTitle,
    newContentAvailable,
    scrollToBottom,
    setIsNearBottom,
    isFinalizing,
    isThinking,
    isStreaming,
    transcriptVersion,
    isLoading,
    isError,
    notFound,
    sessionKey,
    sendFollowup,
    followupIsPending,
    contextWindowUsed,
    contextWindowSize,
    contextUsagePercent,
    healthStatus,
    hasRecoveryActions,
    recoverySessionName,
    runtimeSessionLineage,
    viewedRuntimeSessionId,
    buildLineageTargetPath,
    handleRecoverySuccess,
    issueNumber,
    cancel,
  } = data

  // ── All hooks must be before any early return ──
  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const isNearBottomRef = useRef(true)
  const isUserScrollingRef = useRef(false)
  const isSelectingTextRef = useRef(false)

  const displayStatusKind: StatusKind = isFinalizing && isRunning ? 'finalizing' : statusKind
  const displayTurnCount = meta?.turnCount ?? turns.length

  // Build recovery bar content (not hooks, just derived values)
  const lineageLink = runtimeSessionLineage && runtimeSessionLineage.length >= 2 && buildLineageTargetPath ? (
    <CompactionLineageLink
      runtimeSessionLineage={runtimeSessionLineage}
      viewedRuntimeSessionId={viewedRuntimeSessionId}
      buildTargetPath={buildLineageTargetPath}
    />
  ) : null

  const recoveryBarContent = hasRecoveryActions || lineageLink ? (
    <div className="flex flex-col gap-2">
      {hasRecoveryActions && (
        <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
          <div className="flex-1 min-w-0">
            <ContextHealthBar
              contextWindowUsed={contextWindowUsed}
              contextWindowSize={contextWindowSize}
              contextUsagePercent={contextUsagePercent}
              healthStatus={healthStatus}
            />
          </div>
          {recoverySessionName && (
            <div className="shrink-0">
              <SessionRecoveryActions
                issueNumber={issueNumber}
                sessionName={recoverySessionName}
                status={meta?.status ?? null}
                onSuccess={handleRecoverySuccess}
                bare
              />
            </div>
          )}
        </div>
      )}
      {lineageLink}
    </div>
  ) : null

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
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container || !isNearBottomRef.current || isUserScrollingRef.current || isSelectingTextRef.current) return
    container.scrollTop = container.scrollHeight
  }, [isRunning, transcriptVersion])

  useEffect(() => {
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container || !isNearBottomRef.current || isUserScrollingRef.current || isSelectingTextRef.current) return
    container.scrollTop = container.scrollHeight
  }, [isRunning, meta?.status])

  useEffect(() => {
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    const handleResize = () => {
      if (isNearBottomRef.current && !isUserScrollingRef.current && !isSelectingTextRef.current) {
        container.scrollTop = container.scrollHeight
      }
    }
    window.addEventListener('resize', handleResize)
    return () => window.removeEventListener('resize', handleResize)
  }, [isRunning])

  // ── Early returns (hooks above are all unconditional) ──

  const sessionMeta = meta as import('../../../entities/coder-session').SessionMetadata

  const headerWithRecovery = (
    <SessionHeader
      backPath={backPath}
      backLabel={backLabel}
      issueTitle={issueTitle}
      meta={sessionMeta}
      statusKind={displayStatusKind}
      turnCount={displayTurnCount}
      recoveryBar={recoveryBarContent}
      siblingNav={siblingNav}
      isRunning={isRunning}
      cancel={cancel}
    />
  )

  const headerWithoutRecovery = (
    <SessionHeader
      backPath={backPath}
      backLabel={backLabel}
      issueTitle={issueTitle}
      meta={sessionMeta}
      statusKind={displayStatusKind}
      turnCount={displayTurnCount}
      siblingNav={siblingNav}
      isRunning={isRunning}
      cancel={cancel}
    />
  )

  if (notFound) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-center space-y-3">
          <div className="text-gray-400 text-lg">Session not found</div>
        </div>
      </div>
    )
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-gray-400">Loading session...</div>
      </div>
    )
  }

  if (isError || !meta) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-center space-y-3">
          <div className="text-red-400 text-lg">Failed to load session</div>
          <p className="text-gray-500 text-sm">An error occurred while fetching session data.</p>
        </div>
      </div>
    )
  }

  if (turns.length === 0 && isRunning) {
    return (
      <div className="flex flex-col flex-1 min-h-0 xl:flex-row">
        <div className="flex flex-col flex-1 min-h-0">
          {headerWithRecovery}
          <SessionUsageSummary usage={meta?.usage} />
          <SessionWaitingState />
          <SessionFollowupComposer onSend={sendFollowup} isSending={followupIsPending} disabled={!isRunning} />
        </div>
        {siblingSidebar}
      </div>
    )
  }

  if (turns.length === 0) {
    return (
      <div className="flex flex-col flex-1 min-h-0 xl:flex-row">
        <div className="flex flex-col flex-1 min-h-0">
          {headerWithRecovery}
          <SessionUsageSummary usage={meta?.usage} />
          <SessionEmptyState />
        </div>
        {siblingSidebar}
      </div>
    )
  }

  return (
    <div className="flex flex-col flex-1 min-h-0 relative xl:flex-row">
      <div className="flex flex-col flex-1 min-h-0">
        {headerWithoutRecovery}
        <SessionUsageSummary usage={meta.usage} />
        <div
          ref={scrollContainerRef}
          className="flex-1 overflow-y-auto min-w-0"
          data-testid="session-transcript-scroll-container"
        >
          <StickySessionTitle
            meta={sessionMeta}
            statusKind={displayStatusKind}
            turnCount={displayTurnCount}
          />
          {recoveryBarContent && (
            <div
              data-testid="session-recovery-bar"
              data-sticky="true"
              className="sticky top-9 z-20 border-b border-gray-200 bg-white px-4 py-3"
            >
              {recoveryBarContent}
            </div>
          )}
          <SessionTranscriptLayout
            title={meta.sessionName ?? sessionKey ?? 'Session'}
            turnCount={displayTurnCount}
            turns={displayTurns}
            statusKind={displayStatusKind}
            isRunning={isRunning}
            isThinking={isThinking}
            isStreaming={isStreaming}
            scrollContainerRef={scrollContainerRef}
          />
        </div>

        <SessionFollowupComposer onSend={sendFollowup} isSending={followupIsPending} disabled={!isRunning} />

        {newContentAvailable && <JumpToBottomButton onClick={handleScrollToBottom} />}
      </div>
      {siblingSidebar}
    </div>
  )
}

// ── Sub-components ──

function formatDuration(ms: number): string {
  if (ms < 0) return '0s'
  const totalSec = Math.floor(ms / 1000)
  if (totalSec < 60) return `${totalSec}s`
  const min = Math.floor(totalSec / 60)
  const sec = totalSec % 60
  if (min < 60) return `${min}m ${String(sec).padStart(2, '0')}s`
  const hr = Math.floor(min / 60)
  const remMin = min % 60
  return `${hr}h ${String(remMin).padStart(2, '0')}m`
}

function formatRelativeTime(dateStr: string | null | undefined): string {
  if (!dateStr) return 'never'
  const date = new Date(dateStr)
  const now = Date.now()
  const diff = now - date.getTime()
  if (diff < 60000) return 'just now'
  if (diff < 3600000) return `${Math.floor(diff / 60000)}m ago`
  if (diff < 86400000) return `${Math.floor(diff / 3600000)}h ago`
  return date.toLocaleDateString()
}

function getStageLabel(stage: string | null): string {
  if (!stage) return 'Session'
  return stage.charAt(0).toUpperCase() + stage.slice(1)
}

function StatusBadge({ kind, failureReason }: { kind: StatusKind; failureReason?: string | null }) {
  const config: Record<StatusKind, { label: string; color: string; dot?: boolean }> = {
    loading: { label: 'Loading', color: 'bg-gray-100 text-gray-600' },
    live: { label: 'Running', color: 'bg-blue-100 text-blue-700', dot: true },
    probing: { label: 'Checking session', color: 'bg-yellow-100 text-yellow-700', dot: true },
    finalizing: { label: 'Finalizing', color: 'bg-yellow-100 text-yellow-700' },
    completed: { label: 'Completed', color: 'bg-green-100 text-green-700' },
    failed: { label: 'Session failed', color: 'bg-red-100 text-red-700' },
    stale: { label: 'Stale', color: 'bg-orange-100 text-orange-700' },
  }
  const { label, color, dot } = config[kind]
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${color}`}>
      {dot && (
        <span className="relative flex h-2 w-2">
          <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-current opacity-75" />
          <span className="relative inline-flex rounded-full h-2 w-2 bg-current" />
        </span>
      )}
      {label}
      {kind === 'failed' && failureReason && (
        <span className="ml-1 text-red-500 truncate max-w-[200px]" title={failureReason}>
          {failureReason}
        </span>
      )}
    </span>
  )
}

function SessionHeader({
  backPath,
  backLabel,
  issueTitle,
  meta,
  statusKind,
  turnCount,
  recoveryBar,
  siblingNav,
  isRunning,
  cancel,
}: {
  backPath: string
  backLabel: string
  issueTitle?: string
  meta: import('../../../entities/coder-session').SessionMetadata
  statusKind: StatusKind
  turnCount: number
  recoveryBar?: React.ReactNode
  siblingNav?: React.ReactNode
  isRunning: boolean
  cancel: SessionDataSourceResult['cancel']
}) {
  const isTerminal = statusKind === 'completed' || statusKind === 'failed'
  const [cancelDialogOpen, setCancelDialogOpen] = useState(false)
  const showCancelControl = cancel != null && isRunning
  const createdAt = meta?.createdAt ?? new Date().toISOString()
  const completedAt = meta?.completedAt ?? null
  const duration = isTerminal && completedAt
    ? new Date(completedAt).getTime() - new Date(createdAt).getTime()
    : isTerminal
      ? Date.now() - new Date(createdAt).getTime()
      : 0

  const changedFiles = meta?.changedFiles
  const fileSummary = changedFiles && changedFiles.length > 0
    ? changedFiles.length === 1 ? '1 file changed' : `${changedFiles.length} files changed`
    : null

  const usage = meta?.usage
  const eventSummary = meta?.eventSummary
  const hasUsage =
    usage?.totalTokens != null ||
    usage?.inputTokens != null ||
    usage?.outputTokens != null ||
    usage?.cachedReadTokens != null ||
    usage?.thoughtTokens != null

  const contextWindowPct =
    usage?.contextUsagePercent != null
      ? Math.round(Math.max(0, Math.min(100, usage.contextUsagePercent)))
      : null

  return (
    <div className="border-b border-gray-200 bg-white px-4 py-3 shrink-0 min-w-0">
      <div className="flex flex-wrap items-center gap-2 text-sm mb-2 min-w-0">
        <Link
          to={backPath}
          className="flex items-center gap-1 text-blue-600 hover:text-blue-800 transition-colors whitespace-nowrap shrink-0"
        >
          <ChevronLeftIcon className="h-4 w-4 shrink-0" />
          <span>{backLabel}</span>
        </Link>
        {issueTitle && (
          <>
            <span className="text-gray-300 shrink-0">/</span>
            <span className="text-gray-500 truncate min-w-0">{issueTitle}</span>
          </>
        )}
        {siblingNav && (
          <div className="ml-auto flex max-w-full min-w-0 flex-wrap items-center gap-1" data-testid="session-sibling-navigation-slot">
            {siblingNav}
          </div>
        )}
      </div>

      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:gap-3">
        <div className="flex items-center gap-2 min-w-0">
          <h1 className="text-lg font-semibold text-gray-900 truncate">
            {meta.sessionName ?? 'Session'}
          </h1>
        </div>

        <div className="flex flex-col gap-2 text-xs text-gray-500 sm:flex-row sm:items-center sm:gap-2 sm:ml-auto sm:shrink-0 sm:flex-wrap sm:justify-end">
          <StatusBadge kind={statusKind} failureReason={meta?.failureReason} />
          <span className="px-2 py-0.5 rounded-full bg-gray-100 text-gray-600 font-medium self-start sm:self-auto">
            {getStageLabel(meta?.stage ?? null)}
          </span>

          {meta?.model && eventSummary?.resolvedModel && meta.model !== eventSummary.resolvedModel ? (
            <span className="text-gray-500">
              {meta.model} <span className="text-gray-300">→</span>{' '}
              <span className="text-blue-600">{eventSummary.resolvedModel}</span>
            </span>
          ) : meta?.model ? (
            <span>{meta.model}</span>
          ) : null}

          <span className="text-gray-300">·</span>
          <span>{turnCount} turn{turnCount !== 1 ? 's' : ''}</span>

          {meta?.lastActivityAt && (
            <>
              <span className="text-gray-300">·</span>
              <span title={`Last activity: ${meta.lastActivityAt}`}>
                {formatRelativeTime(meta.lastActivityAt)}
              </span>
            </>
          )}
          {statusKind === 'probing' && meta?.probeSentAt && (
            <>
              <span className="text-gray-300">·</span>
              <span className="text-yellow-600" title={`Probe sent: ${meta.probeSentAt}`}>
                Checking since {formatRelativeTime(meta.probeSentAt)}
              </span>
            </>
          )}
          {fileSummary && (
            <>
              <span className="text-gray-300">·</span>
              <span>{fileSummary}</span>
            </>
          )}
          {isTerminal && (
            <>
              <span className="text-gray-300">·</span>
              <span className={statusKind === 'failed' ? 'text-red-600' : ''}>
                {formatDuration(duration)}
              </span>
            </>
          )}
          {meta?.sessionId && (
            <>
              <span className="text-gray-300">·</span>
              <span className="font-mono text-gray-400 text-xs">{meta.sessionId.slice(0, 8)}</span>
            </>
          )}
          {showCancelControl && (
            <Button
              variant="destructive"
              size="sm"
              onClick={() => setCancelDialogOpen(true)}
              data-testid="session-cancel-trigger"
              aria-label="Cancel session"
              type="button"
            >
              <CircleStopIcon className="h-3.5 w-3.5" aria-hidden="true" />
              Cancel session
            </Button>
          )}
        </div>
      </div>

      {showCancelControl && cancel && (
        <AlertDialog
          open={cancelDialogOpen}
          onOpenChange={(open) => {
            if (cancel.isPending) return
            setCancelDialogOpen(open)
          }}
          title="Cancel this session?"
          description="The agent will be asked to stop. The session may still take a moment to wind down."
          confirmLabel="Cancel session"
          cancelLabel="Keep running"
          tone="destructive"
          loading={cancel.isPending}
          onConfirm={() => {
            cancel.mutate({ onSettled: () => setCancelDialogOpen(false) })
          }}
          data-testid="session-cancel-alert"
        />
      )}

      {(hasUsage || usage?.costAmount != null || usage?.contextWindowUsed != null || eventSummary?.failureCategory || eventSummary?.toolCallCount != null) && (
        <div className="flex items-center gap-3 mt-2 text-xs text-gray-500 flex-wrap">
          {hasUsage && (
            <span>
              {usage?.totalTokens != null
                ? `${formatCompact(usage.totalTokens)} tokens`
                : [usage?.inputTokens != null ? `${formatCompact(usage.inputTokens)} in` : '', usage?.outputTokens != null ? `${formatCompact(usage.outputTokens)} out` : '']
                    .filter(Boolean)
                    .join(' · ')}
              {usage?.cachedReadTokens != null && usage.cachedReadTokens > 0 && (
                <span className="ml-1 text-gray-400">+{formatCompact(usage.cachedReadTokens)} cached</span>
              )}
              {usage?.thoughtTokens != null && usage.thoughtTokens > 0 && (
                <span className="ml-1 text-gray-400">+{formatCompact(usage.thoughtTokens)} thought</span>
              )}
            </span>
          )}
          {usage?.costAmount != null && usage?.costCurrency && (
            <span>{formatCost(usage.costAmount, usage.costCurrency)}</span>
          )}
          {usage?.contextWindowUsed != null && (
            <span>
              {usage?.contextWindowSize != null
                ? `${formatCompact(usage.contextWindowUsed)} / ${formatCompact(usage.contextWindowSize)} ctx`
                : `${formatCompact(usage.contextWindowUsed)} ctx used`}
              {contextWindowPct != null && <span className="ml-1 text-gray-400">({contextWindowPct}%)</span>}
            </span>
          )}
          {eventSummary?.failureCategory && (
            <span className="px-1.5 py-0.5 rounded-full bg-red-50 text-red-600 text-[10px] font-medium">
              {eventSummary.failureCategory}
            </span>
          )}
          {eventSummary?.toolCallCount != null && (
            <span className={eventSummary?.toolErrorCount ? 'text-orange-600 font-medium' : ''}>
              {eventSummary.toolCallCount} tool{eventSummary.toolCallCount !== 1 ? 's' : ''}
              {eventSummary?.toolErrorCount ? ` · ${eventSummary.toolErrorCount} error${eventSummary.toolErrorCount !== 1 ? 's' : ''}` : ''}
            </span>
          )}
        </div>
      )}

      {recoveryBar && (
        <div className="mt-3 pt-3 border-t border-gray-100" data-testid="session-recovery-bar">
          {recoveryBar}
        </div>
      )}
    </div>
  )
}

function StickySessionTitle({ meta, statusKind, turnCount }: {
  meta: import('../../../entities/coder-session').SessionMetadata
  statusKind: StatusKind
  turnCount: number
}) {
  const usage = meta?.usage
  const totalTokens = usage?.totalTokens ?? null
  const contextPct = usage?.contextUsagePercent != null
    ? Math.round(Math.max(0, Math.min(100, usage.contextUsagePercent)))
    : null

  return (
    <div className="sticky top-0 z-20 border-b border-gray-200 bg-white px-4 py-2" data-testid="session-sticky-title">
      <div className="flex items-center gap-2 text-sm">
        <span className="font-medium truncate">{meta?.sessionName ?? 'Session'}</span>
        <StatusBadge kind={statusKind} />
        <span className="text-gray-400 text-xs">{turnCount} turn{turnCount !== 1 ? 's' : ''}</span>
        {totalTokens != null && (
          <>
            <span className="text-gray-300">·</span>
            <span className="text-gray-500 text-xs">{formatCompact(totalTokens)} tokens</span>
          </>
        )}
        {contextPct != null && (
          <>
            <span className="text-gray-300">·</span>
            <span className="text-gray-500 text-xs">{contextPct}% ctx</span>
          </>
        )}
      </div>
    </div>
  )
}

function SessionWaitingState() {
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-blue-400 text-lg">Waiting for activity...</div>
        <p className="text-gray-500 text-sm">The session has started but no activity recorded yet.</p>
      </div>
    </div>
  )
}

function SessionEmptyState() {
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-gray-400 text-lg">No activity recorded for this session</div>
        <p className="text-gray-500 text-sm">This session has no recorded transcript data.</p>
      </div>
    </div>
  )
}

function JumpToBottomButton({ onClick }: { onClick: () => void }) {
  return (
    <Button
      onClick={onClick}
      className="absolute bottom-4 right-4 rounded-full bg-gray-800 text-xs text-white shadow-lg hover:bg-gray-700"
    >
      <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 17a.75.75 0 01-.75-.75V5.612L5.29 9.77a.75.75 0 01-1.08-1.04l5.25-5.5a.75.75 0 011.08 0l5.25 5.5a.75.75 0 11-1.08 1.04l-3.96-4.158V16.25A.75.75 0 0110 17z" clipRule="evenodd" />
      </svg>
      Jump to bottom
    </Button>
  )
}
