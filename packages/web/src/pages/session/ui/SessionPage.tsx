import { useEffect, useRef, useCallback, useMemo } from 'react'
import { useParams, useSearchParams, Link } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react'
import { useIssue } from '../../../entities/issue'
import { useCoderSessions } from '../../../entities/coder-session'
import { getAgentSessionMetadata, getAgentSessionTranscript } from '../../../entities/coder-session'
import type { WorkflowRunSession } from '../../../entities/coder-session'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { formatCompact, formatCost } from '../../../shared/lib/format-compact'
import { useProject, useProjectPath } from '../../../entities/project'
import { useSessionTranscript, projectTurn } from '../../../widgets/session-transcript'
import type { AgentSessionMetadata, AgentSessionTranscriptResponse, SessionMetadata, SessionStatusKind, CoderSessionDetail, SessionTurn } from '../../../entities/coder-session'
import { SessionTranscriptLayout } from '../../../widgets/session-transcript'
import { SessionRecoveryActions } from '../../../widgets/coder-session'
import { SessionFollowupComposer } from '../../../widgets/coder-session'
import { ContextHealthBar, CompactionLineageLink } from '../../../widgets/session-health'
import { useSiblingSessions } from '../../../widgets/issue-workflow'
import { Button } from '@/shared/ui/components/button'

type StatusKind = SessionStatusKind

const EMPTY_TURNS: SessionTurn[] = []

function buildSessionMetadata(
  meta: AgentSessionMetadata,
  lastEventAt: string | null,
  turnCount: number,
  acpSessionId: string,
): SessionMetadata {
  const isRunning = meta.status === 'active' || meta.status === 'running' || meta.status === 'probing'
  return {
    sessionId: meta.id,
    sessionName: meta.sessionName,
    coderSessionId: meta.id,
    issueId: '',
    acpSessionId: meta.acpSessionId ?? acpSessionId,
    executionId: null,
    title: meta.title,
    status: meta.status,
    statusKind: meta.statusKind
      ?? getSessionStatusKind(meta.status, lastEventAt ?? meta.lastActivityAt, isRunning, meta.completedAt),
    model: meta.model,
    stage: meta.stage,
    createdAt: meta.createdAt,
    completedAt: meta.completedAt,
    lastActivityAt: lastEventAt ?? meta.lastActivityAt ?? meta.lastDataAt ?? null,
    firstPromptSentAt: null,
    lastDataAt: lastEventAt ?? meta.lastDataAt ?? meta.lastActivityAt ?? null,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
    partCount: meta.metadata.partCount,
    toolCount: meta.metadata.toolCount,
    turnCount,
    changedFiles: meta.changedFiles,
    eventSummary: meta.eventSummary
      ? {
          resolvedModel: meta.eventSummary.resolvedModel ?? null,
          failureCategory: meta.eventSummary.failureCategory ?? null,
          toolCallCount: meta.eventSummary.toolCallCount ?? null,
          toolErrorCount: meta.eventSummary.toolErrorCount ?? null,
        }
      : undefined,
    usage: meta.usage
      ? {
          inputTokens: meta.usage.inputTokens ?? null,
          outputTokens: meta.usage.outputTokens ?? null,
          totalTokens: meta.usage.totalTokens ?? null,
          cachedReadTokens: meta.usage.cachedReadTokens ?? null,
          thoughtTokens: meta.usage.thoughtTokens ?? null,
          costAmount: meta.usage.costAmount ?? null,
          costCurrency: meta.usage.costCurrency ?? null,
          contextWindowUsed: meta.usage.contextWindowUsed ?? null,
          contextWindowSize: meta.usage.contextWindowSize ?? null,
        }
      : undefined,
  }
}

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

function getSessionStatusKind(
  rawStatus: string | undefined,
  lastActivityAt: string | null | undefined,
  isRunning: boolean,
  completedAt?: string | null,
): StatusKind {
  if (rawStatus === 'failed' || rawStatus === 'timeout' || rawStatus === 'cancelled') {
    return 'failed'
  }
  if (rawStatus === 'completed') return 'completed'
  if (rawStatus === 'inactive') return 'stale'
  if (rawStatus === 'probing') return 'probing'
  if (rawStatus === 'active') return lastActivityAt ? 'live' : 'stale'
  if (isRunning && completedAt) return 'finalizing'
  if (!isRunning) {
    return 'completed'
  }
  if (!lastActivityAt) return 'live'
  const lastActivity = new Date(lastActivityAt).getTime()
  const now = Date.now()
  const twoMinutes = 2 * 60 * 1000
  if (now - lastActivity > twoMinutes) return 'stale'
  return 'live'
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

function SessionNotFound({ issueNumber }: { issueNumber: number }) {
  const toProjectPath = useProjectPath()
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-gray-400 text-lg">Session not found</div>
        <Link
          to={toProjectPath(`/issues/${issueNumber}`)}
          className="text-sm text-blue-600 hover:text-blue-800 underline"
        >
          Back to issue #{issueNumber}
        </Link>
      </div>
    </div>
  )
}

function SessionLoadingState() {
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-gray-400">Loading session...</div>
    </div>
  )
}

function SessionApiErrorState({ issueNumber }: { issueNumber: number }) {
  const toProjectPath = useProjectPath()
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-red-400 text-lg">Failed to load session</div>
        <p className="text-gray-500 text-sm">An error occurred while fetching session data.</p>
        <Link
          to={toProjectPath(`/issues/${issueNumber}`)}
          className="text-sm text-blue-600 hover:text-blue-800 underline"
        >
          Back to issue #{issueNumber}
        </Link>
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

function SessionEmptyState({ issueNumber }: { issueNumber: number }) {
  const toProjectPath = useProjectPath()
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-gray-400 text-lg">No activity recorded for this session</div>
        <p className="text-gray-500 text-sm">This session has no recorded transcript data.</p>
        <Link
          to={toProjectPath(`/issues/${issueNumber}`)}
          className="text-sm text-blue-600 hover:text-blue-800 underline"
        >
          Back to issue #{issueNumber}
        </Link>
      </div>
    </div>
  )
}

function SessionLegacyMissingState() {
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center space-y-3">
        <div className="text-yellow-500 text-lg">Incomplete Session Data</div>
        <p className="text-gray-500 text-sm">Prompt was not recorded for this historical session.</p>
        <p className="text-gray-400 text-xs">Only activity logs are available.</p>
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

interface SiblingNavigationProps {
  issueNumber: number
  previous: WorkflowRunSession | null
  next: WorkflowRunSession | null
}

function SiblingNavigation({ issueNumber, previous, next }: SiblingNavigationProps) {
  const toProjectPath = useProjectPath()
  const previousPath = previous
    ? toProjectPath(`/issues/${issueNumber}/workflow/sessions/${encodeURIComponent(previous.sessionName)}`)
    : null
  const nextPath = next
    ? toProjectPath(`/issues/${issueNumber}/workflow/sessions/${encodeURIComponent(next.sessionName)}`)
    : null

  return (
    <div className="flex items-center gap-1" data-testid="session-sibling-navigation">
      {previous ? (
        <Link
          to={previousPath!}
          className="inline-flex items-center gap-1 rounded border border-gray-200 bg-white px-2 py-1 text-xs font-medium text-gray-700 transition-colors hover:border-blue-300 hover:bg-blue-50 hover:text-blue-700"
          data-testid="session-sibling-prev"
          title={`Previous session: ${previous.sessionName}`}
          aria-label={`Previous session: ${previous.sessionName}`}
        >
          <ChevronLeftIcon className="h-3.5 w-3.5" aria-hidden="true" />
          <span className="font-mono">prev: {previous.sessionName}</span>
        </Link>
      ) : (
        <span
          className="inline-flex items-center gap-1 rounded border border-gray-100 bg-gray-50 px-2 py-1 text-xs font-medium text-gray-300 cursor-not-allowed"
          data-testid="session-sibling-prev-disabled"
          aria-disabled="true"
          title="No previous session"
        >
          <ChevronLeftIcon className="h-3.5 w-3.5" aria-hidden="true" />
          <span className="font-mono">prev</span>
        </span>
      )}
      {next ? (
        <Link
          to={nextPath!}
          className="inline-flex items-center gap-1 rounded border border-gray-200 bg-white px-2 py-1 text-xs font-medium text-gray-700 transition-colors hover:border-blue-300 hover:bg-blue-50 hover:text-blue-700"
          data-testid="session-sibling-next"
          title={`Next session: ${next.sessionName}`}
          aria-label={`Next session: ${next.sessionName}`}
        >
          <span className="font-mono">next: {next.sessionName}</span>
          <ChevronRightIcon className="h-3.5 w-3.5" aria-hidden="true" />
        </Link>
      ) : (
        <span
          className="inline-flex items-center gap-1 rounded border border-gray-100 bg-gray-50 px-2 py-1 text-xs font-medium text-gray-300 cursor-not-allowed"
          data-testid="session-sibling-next-disabled"
          aria-disabled="true"
          title="No next session"
        >
          <span className="font-mono">next</span>
          <ChevronRightIcon className="h-3.5 w-3.5" aria-hidden="true" />
        </span>
      )}
    </div>
  )
}

interface SiblingSessionsSidebarProps {
  issueNumber: number
  siblings: WorkflowRunSession[]
  currentKey: string | null
}

export function isCurrentSiblingSession(sibling: Pick<WorkflowRunSession, 'id' | 'sessionName'>, currentKey: string | null): boolean {
  return sibling.sessionName === currentKey || sibling.id === currentKey
}

function SiblingSessionsSidebar({ issueNumber, siblings, currentKey }: SiblingSessionsSidebarProps) {
  const toProjectPath = useProjectPath()
  if (siblings.length === 0) return null

  return (
    <aside
      className="hidden xl:flex w-64 shrink-0 flex-col border-l border-gray-200 bg-white"
      data-testid="session-sibling-sidebar"
      aria-label="Sibling sessions"
    >
      <div className="px-3 py-2 border-b border-gray-200 text-xs font-semibold uppercase tracking-wide text-gray-500">
        Sibling sessions
      </div>
      <nav className="flex-1 overflow-y-auto p-1">
        {siblings.map((sibling) => {
          const isCurrent = isCurrentSiblingSession(sibling, currentKey)
          const path = toProjectPath(`/issues/${issueNumber}/workflow/sessions/${encodeURIComponent(sibling.sessionName)}`)
          const baseClass = 'flex items-center gap-2 rounded px-2 py-1.5 text-xs transition-colors min-w-0'
          const stateClass = isCurrent
            ? 'bg-blue-50 text-blue-800 font-medium'
            : 'text-gray-700 hover:bg-gray-100'
          return (
            <Link
              key={sibling.id}
              to={path}
              className={`${baseClass} ${stateClass}`}
              data-testid="session-sibling-sidebar-entry"
              data-current={isCurrent ? 'true' : 'false'}
              title={`Open ${sibling.sessionName} transcript`}
              aria-current={isCurrent ? 'page' : undefined}
            >
              <span
                className={`inline-block h-1.5 w-1.5 shrink-0 rounded-full ${
                  sibling.status === 'completed'
                    ? 'bg-green-500'
                    : sibling.status === 'failed' || sibling.status === 'cancelled'
                      ? 'bg-red-500'
                      : sibling.status === 'running' || sibling.status === 'active' || sibling.status === 'probing'
                        ? 'bg-blue-500'
                        : 'bg-gray-400'
                }`}
                aria-hidden="true"
              />
              <span className="min-w-0 flex-1 truncate font-mono">{sibling.sessionName}</span>
              {isCurrent && (
                <span className="shrink-0 text-[10px] uppercase tracking-wide text-blue-700">current</span>
              )}
            </Link>
          )
        })}
      </nav>
    </aside>
  )
}

interface SessionHeaderProps {
  issueNumber: number
  issueTitle?: string
  meta: CoderSessionDetail['metadata']
  statusKind: StatusKind
  turnCount: number
  recoveryBar?: React.ReactNode
  siblingNav?: React.ReactNode
}

function SessionHeader({ issueNumber, issueTitle, meta, statusKind, turnCount, recoveryBar, siblingNav }: SessionHeaderProps) {
  const toProjectPath = useProjectPath()
  const isTerminal = statusKind === 'completed' || statusKind === 'failed'
  const createdAt = meta?.createdAt ?? new Date().toISOString()
  const completedAt = meta?.completedAt ?? null
  const duration = isTerminal && completedAt
    ? new Date(completedAt).getTime() - new Date(createdAt).getTime()
    : isTerminal
      ? Date.now() - new Date(createdAt).getTime()
      : 0

  const changedFiles = meta?.changedFiles
  const fileSummary = changedFiles && changedFiles.length > 0
    ? changedFiles.length === 1
      ? `1 file changed`
      : `${changedFiles.length} files changed`
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
    usage?.contextWindowUsed != null && usage?.contextWindowSize != null && usage.contextWindowSize > 0
      ? Math.min(100, Math.round((usage.contextWindowUsed / usage.contextWindowSize) * 100))
      : null

  return (
    <div className="border-b border-gray-200 bg-white px-4 py-3 shrink-0 min-w-0">
      <div className="flex flex-wrap items-center gap-2 text-sm mb-2 min-w-0">
        <Link
          to={toProjectPath(`/issues/${issueNumber}`)}
          className="flex items-center gap-1 text-blue-600 hover:text-blue-800 transition-colors whitespace-nowrap shrink-0"
        >
          <svg className="h-4 w-4 shrink-0" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z" clipRule="evenodd" />
          </svg>
          <span>Issue #{issueNumber}</span>
        </Link>
        {issueTitle && (
          <>
            <span className="text-gray-300 shrink-0">/</span>
            <span className="text-gray-500 truncate min-w-0">{issueTitle}</span>
          </>
        )}
        {siblingNav && (
          <div className="ml-auto shrink-0 flex items-center gap-1" data-testid="session-sibling-navigation-slot">
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

          {/* Model badges */}
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
        </div>
      </div>

      {/* Observability bar */}
      {(hasUsage || usage?.costAmount != null || usage?.contextWindowUsed != null || eventSummary?.failureCategory || eventSummary?.toolCallCount != null) && (
        <div className="flex items-center gap-3 mt-2 text-xs text-gray-500 flex-wrap">
          {hasUsage && (
            <span>
              {usage?.totalTokens != null
                ? `${formatCompact(usage.totalTokens)} tokens`
                : [
                    usage?.inputTokens != null ? `${formatCompact(usage.inputTokens)} in` : '',
                    usage?.outputTokens != null ? `${formatCompact(usage.outputTokens)} out` : '',
                  ]
                    .filter(Boolean)
                    .join(' · ')}
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
              {contextWindowPct != null && (
                <span className="ml-1 text-gray-400">({contextWindowPct}%)</span>
              )}
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

export function SessionPage() {
  const { number: numberStr, sessionId, sessionName } = useParams<{ number: string; sessionId?: string; sessionName?: string }>()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()
  const queryClient = useQueryClient()
  const issueNumber = Number(numberStr)
  const decodedSessionId = sessionId ? decodeURIComponent(sessionId) : undefined
  const decodedSessionName = sessionName ? decodeURIComponent(sessionName) : undefined
  const routeSessionKey = decodedSessionName ?? decodedSessionId

  useDocumentTitle(`Session — Issue #${issueNumber} — Mohist`)

  const { data: issue } = useIssue(issueNumber)
  const { sessions, isLoading: sessionsLoading } = useCoderSessions(issueNumber)
  const session = sessions.find((s) => decodedSessionName
    ? (s.sessionName ?? s.executionId ?? s.id) === decodedSessionName
    : s.id === decodedSessionId)

  const siblingNav = useSiblingSessions(issue?.workflowRunId ?? null, {
    currentKey: decodedSessionName ?? decodedSessionId ?? null,
  })

  const routeSessionLookup = decodedSessionName ?? routeSessionKey
  const lookupKey = decodedSessionName ?? decodedSessionId

  const hasRoute = !!routeSessionLookup && !!projectId && issueNumber > 0
  const metadataQueryKey = useMemo(
    () => ['issues', issueNumber, projectId, 'agent-session-metadata', lookupKey] as const,
    [issueNumber, projectId, lookupKey],
  )
  const transcriptQueryKey = useMemo(
    () => ['issues', issueNumber, projectId, 'agent-session-transcript', lookupKey] as const,
    [issueNumber, projectId, lookupKey],
  )

  const handleRecoverySuccess = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: metadataQueryKey })
    queryClient.invalidateQueries({ queryKey: transcriptQueryKey })
    queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'coder-sessions'] })
  }, [queryClient, metadataQueryKey, transcriptQueryKey, issueNumber])

  const {
    data: metadata,
    isLoading: metadataLoading,
    isError: metadataError,
  } = useQuery<AgentSessionMetadata | null, Error>({
    queryKey: metadataQueryKey,
    queryFn: async () => {
      if (!routeSessionLookup) return null
      return getAgentSessionMetadata(issueNumber, routeSessionLookup, projectId)
    },
    enabled: hasRoute,
  })

  const {
    data: transcriptResponse,
  } = useQuery<AgentSessionTranscriptResponse | null, Error>({
    queryKey: transcriptQueryKey,
    queryFn: async () => {
      if (!routeSessionLookup) return null
      return getAgentSessionTranscript(issueNumber, routeSessionLookup, projectId)
    },
    enabled: hasRoute && !!metadata,
  })

  const initialTurns = useMemo<SessionTurn[]>(() => transcriptResponse?.turns ?? EMPTY_TURNS, [transcriptResponse])

  const lastEventAt = useMemo(() => {
    return transcriptResponse?.lastActivityAt ?? metadata?.lastActivityAt ?? metadata?.lastDataAt ?? null
  }, [transcriptResponse, metadata?.lastActivityAt, metadata?.lastDataAt])

  const detail: CoderSessionDetail | null = useMemo(() => {
    if (!metadata) return null
    const turnCount = transcriptResponse?.turns.length ?? 0
    return {
      id: metadata.id,
      acpSessionId: metadata.acpSessionId,
      executionId: null,
      taskDescription: metadata.title,
      status: metadata.status,
      createdAt: metadata.createdAt,
      completedAt: metadata.completedAt,
      model: metadata.model,
      coderType: null,
      stage: metadata.stage,
      title: metadata.title,
      metadata: buildSessionMetadata(metadata, lastEventAt, turnCount, metadata.acpSessionId),
      turns: initialTurns,
      incomplete: false,
    }
  }, [metadata, transcriptResponse, initialTurns, lastEventAt])

  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const isNearBottomRef = useRef(true)

  const rawStatus = detail?.metadata?.status ?? detail?.status ?? session?.status
  const apiStatusKind = detail?.metadata?.statusKind
  const isRunning = (rawStatus === 'active' || rawStatus === 'running' || rawStatus === 'probing') && apiStatusKind !== 'completed' && apiStatusKind !== 'failed'
  const acpSessionId = detail?.acpSessionId ?? session?.acpSessionId ?? ''

  const statusKind: StatusKind = detail
    ? (detail.metadata.statusKind ?? getSessionStatusKind(rawStatus, detail.metadata.lastActivityAt, isRunning, detail.metadata.completedAt ?? detail.completedAt))
    : getSessionStatusKind(rawStatus, undefined, isRunning, session?.completedAt)

  const {
    turns,
    transcriptVersion,
    scrollToBottom,
    newContentAvailable,
    setIsNearBottom,
    isFinalizing,
    isThinking,
    isStreaming,
  } = useSessionTranscript({
    issueNumber,
    sessionId: detail?.id ?? decodedSessionId ?? decodedSessionName ?? '',
    acpSessionId,
    initialTurns: initialTurns.length > 0 ? initialTurns : undefined,
    sessionQueryKeys: [metadataQueryKey, transcriptQueryKey],
    isRunning,
  })

  const displayStatusKind: StatusKind = isFinalizing && isRunning ? 'finalizing' : statusKind
  const displayTurnCount = detail?.metadata?.turnCount ?? turns.length

  const displayTurns = turns.map((turn) => projectTurn(turn))

  const recoverySessionName = detail?.metadata?.sessionName ?? session?.sessionName ?? session?.executionId ?? routeSessionKey ?? ''

  // The lineage link is anchored to the runtime session the user is
  // currently looking at — either the one named by the `?rt=` query
  // param (intra-session runtime facet) or the latest binding (the
  // page default when no `?rt` is present). The page itself always
  // loads the metadata/transcript for the stable Mohist session; the
  // `?rt` value is only used to scope the link to a specific runtime
  // session in the lineage chain.
  const [searchParams] = useSearchParams()
  const runtimeLineage = metadata?.runtimeSessionLineage ?? null
  const viewedRuntimeSessionId = searchParams.get('rt') ?? metadata?.acpSessionId ?? null
  const lineageLink = runtimeLineage && runtimeLineage.length >= 2 ? (
    <CompactionLineageLink
      runtimeSessionLineage={runtimeLineage}
      viewedRuntimeSessionId={viewedRuntimeSessionId}
      buildTargetPath={(runtimeId) => {
        const base = toProjectPath(`/issues/${issueNumber}/workflow/sessions/${encodeURIComponent(recoverySessionName)}`)
        return `${base}?rt=${encodeURIComponent(runtimeId)}`
      }}
    />
  ) : null

  const recoveryBar = recoverySessionName ? (
    <div className="flex flex-col gap-2">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
        <div className="flex-1 min-w-0">
          <ContextHealthBar
            contextWindowUsed={detail?.metadata?.usage?.contextWindowUsed ?? null}
            contextWindowSize={detail?.metadata?.usage?.contextWindowSize ?? null}
            contextUsagePercent={detail?.metadata?.usage?.contextUsagePercent ?? null}
          />
        </div>
        <div className="shrink-0">
          <SessionRecoveryActions
            issueNumber={issueNumber}
            sessionName={recoverySessionName}
            status={detail?.metadata?.status ?? detail?.status ?? session?.status ?? null}
            onSuccess={handleRecoverySuccess}
            bare
          />
        </div>
      </div>
      {lineageLink}
    </div>
  ) : null

  const siblingNavigation = (
    <SiblingNavigation
      issueNumber={issueNumber}
      previous={siblingNav.previous}
      next={siblingNav.next}
    />
  )

  const siblingSidebar = (
    <SiblingSessionsSidebar
      issueNumber={issueNumber}
      siblings={siblingNav.sessions}
      currentKey={decodedSessionName ?? decodedSessionId ?? null}
    />
  )

  const isUserScrollingRef = useRef(false)
  const isSelectingTextRef = useRef(false)

  const handleScroll = useCallback((evt?: Event) => {
    const container = scrollContainerRef.current
    if (!container) return

    const target = evt?.target as HTMLElement | null
    if (target && (target as HTMLElement).closest('[data-scrollable]')) {
      return
    }

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
  }, [setIsNearBottom])

  const handleScrollToBottom = useCallback(() => {
    const container = scrollContainerRef.current
    if (!container) return
    container.scrollTo({
      top: container.scrollHeight,
      behavior: 'smooth',
    })
    scrollToBottom()
    isUserScrollingRef.current = false
  }, [scrollToBottom])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    let animationFrame: number | null = null

    const onScroll = (evt: Event) => {
      if (animationFrame !== null) {
        cancelAnimationFrame(animationFrame)
      }
      animationFrame = requestAnimationFrame(() => {
        handleScroll(evt)
      })
    }

    container.addEventListener('scroll', onScroll, { passive: true })
    return () => {
      container.removeEventListener('scroll', onScroll)
      if (animationFrame !== null) {
        cancelAnimationFrame(animationFrame)
      }
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
    if (!container) return

    if (!isNearBottomRef.current) return
    if (isUserScrollingRef.current || isSelectingTextRef.current) return

    container.scrollTop = container.scrollHeight
  }, [isRunning, transcriptVersion])

  useEffect(() => {
    if (!isRunning) return
    const container = scrollContainerRef.current
    if (!container) return

    if (!isNearBottomRef.current) return
    if (isUserScrollingRef.current || isSelectingTextRef.current) return

    container.scrollTop = container.scrollHeight
  }, [isRunning, detail?.metadata?.status])

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

  if (!routeSessionKey || isNaN(issueNumber) || issueNumber <= 0) {
    return <SessionNotFound issueNumber={issueNumber || 0} />
  }

  if (sessionsLoading || metadataLoading) {
    return <SessionLoadingState />
  }

  if (metadataError || (!metadata && !session)) {
    return <SessionApiErrorState issueNumber={issueNumber} />
  }

  if (!detail) {
    return <SessionNotFound issueNumber={issueNumber} />
  }

  if (detail.incomplete && turns.length === 0) {
    return (
      <div className="flex flex-col flex-1 min-h-0 xl:flex-row">
        <div className="flex flex-col flex-1 min-h-0">
          <SessionHeader
            issueNumber={issueNumber}
            issueTitle={issue?.title}
            meta={detail.metadata}
            statusKind={displayStatusKind}
            turnCount={displayTurnCount}
            recoveryBar={recoveryBar}
            siblingNav={siblingNavigation}
          />
          <SessionLegacyMissingState />
        </div>
        {siblingSidebar}
      </div>
    )
  }

  if (turns.length === 0 && isRunning) {
    return (
      <div className="flex flex-col flex-1 min-h-0 xl:flex-row">
        <div className="flex flex-col flex-1 min-h-0">
          <SessionHeader
            issueNumber={issueNumber}
            issueTitle={issue?.title}
            meta={detail.metadata}
            statusKind={displayStatusKind}
            turnCount={displayTurnCount}
            recoveryBar={recoveryBar}
            siblingNav={siblingNavigation}
          />
          <SessionWaitingState />
          <SessionFollowupComposer
            issueNumber={issueNumber}
            sessionName={routeSessionKey}
            disabled={!isRunning}
          />
        </div>
        {siblingSidebar}
      </div>
    )
  }

  if (turns.length === 0) {
    return (
      <div className="flex flex-col flex-1 min-h-0 xl:flex-row">
        <div className="flex flex-col flex-1 min-h-0">
          <SessionHeader
            issueNumber={issueNumber}
            issueTitle={issue?.title}
            meta={detail.metadata}
            statusKind={displayStatusKind}
            turnCount={displayTurnCount}
            recoveryBar={recoveryBar}
            siblingNav={siblingNavigation}
          />
          <SessionEmptyState issueNumber={issueNumber} />
        </div>
        {siblingSidebar}
      </div>
    )
  }

  return (
    <div className="flex flex-col flex-1 min-h-0 relative xl:flex-row">
      <div className="flex flex-col flex-1 min-h-0">
        <SessionHeader
          issueNumber={issueNumber}
          issueTitle={issue?.title}
          meta={detail.metadata}
          statusKind={displayStatusKind}
          turnCount={displayTurnCount}
          siblingNav={siblingNavigation}
        />
        <div
          ref={scrollContainerRef}
          className="flex-1 overflow-y-auto min-w-0"
          data-testid="session-transcript-scroll-container"
        >
          {recoveryBar && (
            <div
              data-testid="session-recovery-bar"
              data-sticky="true"
              className="sticky top-0 z-20 border-b border-gray-200 bg-white px-4 py-3"
            >
              {recoveryBar}
            </div>
          )}
          <SessionTranscriptLayout
            title={detail.metadata.sessionName ?? routeSessionKey ?? 'Session'}
            turnCount={displayTurnCount}
            turns={displayTurns}
            statusKind={displayStatusKind}
            isRunning={isRunning}
            isThinking={isThinking}
            isStreaming={isStreaming}
            scrollContainerRef={scrollContainerRef}
          />
        </div>

        <SessionFollowupComposer
          issueNumber={issueNumber}
          sessionName={routeSessionKey}
          disabled={!isRunning}
        />

        {newContentAvailable && (
          <JumpToBottomButton onClick={handleScrollToBottom} />
        )}
      </div>
      {siblingSidebar}
    </div>
  )
}
