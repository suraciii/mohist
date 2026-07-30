import { useMemo, useState, type ComponentProps, type ComponentType } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import {
  BotIcon,
  PencilIcon,
  ArchiveIcon,
  PlayIcon,
  ClockIcon,
  XCircleIcon,
  CheckCircleIcon,
  AlertCircleIcon,
  AlertTriangleIcon,
  Loader2Icon,
  RotateCcwIcon,
  HourglassIcon,
} from 'lucide-react'
import {
  useAgent,
  useAgentDetailStatus,
  useAgentSessions,
  useArchiveAgent,
  useUnarchiveAgent,
  readAgentModelAndVariant,
} from '../../../entities/agent'
import type {
  AgentAvailabilityResponse,
  AgentInfo,
  AgentReadinessResult,
  AgentSessionListItemDto,
  AgentStatusDetailResponse,
  AgentWaitingWorkItem,
} from '../../../entities/agent'
import { useProjectPath } from '../../../entities/project'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { Button } from '@/shared/ui/components/button'
import { Badge } from '@/shared/ui/components/badge'
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from '@/shared/ui/components/dialog'
import { AgentProfileEditor as DefaultAgentProfileEditor } from '../../../widgets/agent-profile-editor'
import { SubscriptionsSection as DefaultSubscriptionsSection } from '../../../widgets/agent-subscriptions'

export interface AgentDetailPageComponents {
  AgentProfileEditor: ComponentType<ComponentProps<typeof DefaultAgentProfileEditor>>
  SubscriptionsSection: ComponentType<ComponentProps<typeof DefaultSubscriptionsSection>>
}

export interface AgentDetailPageData {
  agent: AgentInfo | undefined
  isLoading: boolean
  isError: boolean
  sessions: AgentSessionListItemDto[]
  sessionsLoading: boolean
  archiveAgent: Pick<ReturnType<typeof useArchiveAgent>, 'mutate' | 'isPending'>
  unarchiveAgent: Pick<ReturnType<typeof useUnarchiveAgent>, 'mutate' | 'isPending'>
  detailStatus: AgentStatusDetailResponse | undefined
  detailStatusLoading: boolean
}

export type AgentDetailPageDataHook = (agentId: string) => AgentDetailPageData

const useDefaultData: AgentDetailPageDataHook = (agentId) => {
  const { data: agent, isLoading, isError } = useAgent(agentId)
  const { data: sessions = [], isLoading: sessionsLoading } = useAgentSessions({ agentRef: agentId })
  const { data: detailStatus, isLoading: detailStatusLoading } = useAgentDetailStatus(agentId)
  return {
    agent,
    isLoading,
    isError,
    sessions,
    sessionsLoading,
    archiveAgent: useArchiveAgent(),
    unarchiveAgent: useUnarchiveAgent(),
    detailStatus,
    detailStatusLoading,
  }
}

const defaultComponents: AgentDetailPageComponents = {
  AgentProfileEditor: DefaultAgentProfileEditor,
  SubscriptionsSection: DefaultSubscriptionsSection,
}

function formatTime(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  const now = new Date()
  const diffMs = now.getTime() - d.getTime()
  const diffMin = Math.floor(diffMs / 60000)
  if (diffMin < 1) return 'just now'
  if (diffMin < 60) return `${diffMin}m ago`
  const diffHr = Math.floor(diffMin / 60)
  if (diffHr < 24) return `${diffHr}h ago`
  const diffDay = Math.floor(diffHr / 24)
  if (diffDay < 7) return `${diffDay}d ago`
  return d.toLocaleDateString()
}

function statusIcon(activity: string) {
  switch (activity) {
    case 'active':
      return <ClockIcon className="size-3.5 text-blue-500" />
    case 'unknown':
      return <XCircleIcon className="size-3.5 text-red-500" />
    case 'idle':
      return <CheckCircleIcon className="size-3.5 text-emerald-500" />
    default:
      return <AlertCircleIcon className="size-3.5 text-muted-foreground" />
  }
}

function describeWaitingReason(reason: string | null | undefined): string {
  switch (reason) {
    case 'no-online-runner':
      return 'No runner is online'
    case 'capacity-full':
      return 'Runner slots are full'
    case 'concurrency-limit':
      return 'Agent is at its concurrency limit'
    default:
      return 'Waiting'
  }
}

function ReadinessCard({
  readiness,
  toProjectPath,
}: {
  readiness: AgentReadinessResult | undefined
  toProjectPath: (path: string) => string
}) {
  const conclusion = readiness?.conclusion ?? 'Unknown'
  const gaps = readiness?.gaps ?? []
  const setup = readiness?.setup ?? null

  const tone =
    conclusion === 'Ready'
      ? { borderClass: 'border-emerald-200', iconBg: 'bg-emerald-100', iconClass: 'text-emerald-600', icon: <CheckCircleIcon className="size-4" />, labelClass: 'text-emerald-700' }
      : conclusion === 'Needs setup'
        ? { borderClass: 'border-red-200', iconBg: 'bg-red-100', iconClass: 'text-red-600', icon: <AlertTriangleIcon className="size-4" />, labelClass: 'text-red-700' }
        : { borderClass: 'border-amber-200', iconBg: 'bg-amber-100', iconClass: 'text-amber-600', icon: <AlertCircleIcon className="size-4" />, labelClass: 'text-amber-700' }

  return (
    <div
      data-testid="agent-detail-readiness"
      data-conclusion={conclusion}
      className={`rounded-lg border ${tone.borderClass} bg-card p-4 space-y-2`}
    >
      <div className="flex items-center gap-2">
        <span className={`inline-flex items-center justify-center size-6 rounded-full ${tone.iconBg} ${tone.iconClass}`}>
          {tone.icon}
        </span>
        <div className="flex flex-col">
          <span className="text-[10px] font-medium uppercase tracking-wider text-muted-foreground">Readiness</span>
          <span data-testid="agent-detail-readiness-conclusion" className={`text-sm font-semibold ${tone.labelClass}`}>
            {conclusion}
          </span>
        </div>
      </div>
      {conclusion === 'Needs setup' && gaps.length > 0 && (
        <ul data-testid="agent-detail-readiness-gaps" className="space-y-1">
          {gaps.map((gap) => (
            <li
              key={gap.code}
              data-testid={`agent-detail-readiness-gap-${gap.code}`}
              className="rounded-md border border-red-100 bg-red-50/50 px-2 py-1.5 text-xs text-red-900"
            >
              <p className="font-medium">{gap.message}</p>
              <p className="text-red-700/80 mt-0.5">{gap.action}</p>
            </li>
          ))}
        </ul>
      )}
      {conclusion === 'Needs setup' && setup && (
        <p data-testid="agent-detail-readiness-setup" className="text-xs text-muted-foreground">
          Fix in <a className="font-medium text-foreground underline" href={toProjectPath(setup.path)}>{setup.label}</a>.
        </p>
      )}
      {conclusion === 'Unknown' && (
        <p data-testid="agent-detail-readiness-hint" className="text-xs text-muted-foreground">
          The server has not yet confirmed this Agent. New work will wait for validation.
        </p>
      )}
    </div>
  )
}

function AvailabilityCard({
  availability,
  waitingWork,
  loading,
}: {
  availability: AgentAvailabilityResponse | undefined
  waitingWork: AgentWaitingWorkItem[]
  loading: boolean
}) {
  if (loading && !availability) {
    return (
      <div
        data-testid="agent-detail-availability"
        data-state="loading"
        className="rounded-lg border border-border bg-card p-4 text-xs text-muted-foreground"
      >
        Loading availability…
      </div>
    )
  }
  if (!availability) {
    return (
      <div
        data-testid="agent-detail-availability"
        data-state="unavailable"
        className="rounded-lg border border-border bg-card p-4 text-xs text-muted-foreground"
      >
        Availability not yet reported by the server.
      </div>
    )
  }
  const canStartNow = availability.canStartNow
  const reasonText = describeWaitingReason(availability.waitingReason)
  return (
    <div
      data-testid="agent-detail-availability"
      data-state={canStartNow ? 'ready' : 'waiting'}
      data-waiting-reason={availability.waitingReason ?? ''}
      className={`rounded-lg border ${canStartNow ? 'border-emerald-200' : 'border-amber-200'} bg-card p-4 space-y-2`}
    >
      <div className="flex items-center gap-2">
        <span className={`inline-flex items-center justify-center size-6 rounded-full ${canStartNow ? 'bg-emerald-100 text-emerald-600' : 'bg-amber-100 text-amber-600'}`}>
          {canStartNow ? <CheckCircleIcon className="size-4" /> : <HourglassIcon className="size-4" />}
        </span>
        <div className="flex flex-col">
          <span className="text-[10px] font-medium uppercase tracking-wider text-muted-foreground">Availability</span>
          <span data-testid="agent-detail-availability-conclusion" className="text-sm font-semibold text-foreground">
            {canStartNow ? 'Can start now' : `Waiting — ${reasonText}`}
          </span>
        </div>
      </div>
      <p data-testid="agent-detail-availability-detail" className="text-xs text-muted-foreground">
        Active runs: {availability.activeRuns}
        {availability.maxConcurrentRuns != null && ` / ${availability.maxConcurrentRuns}`}
        {' · '}
        Runner slots: {availability.capacity.usedSlots}/{availability.capacity.totalSlots}
      </p>
      {waitingWork.length > 0 && (
        <div data-testid="agent-detail-waiting-work" className="space-y-1 pt-1">
          <h4 className="text-[10px] font-medium uppercase tracking-wider text-muted-foreground">
            Waiting work ({waitingWork.length})
          </h4>
          <ul className="space-y-1">
            {waitingWork.map((item) => (
              <li
                key={item.jobId}
                data-testid={`agent-detail-waiting-work-${item.jobId}`}
                data-waiting-reason={item.waitingReason}
                className="flex items-center gap-2 rounded-md border border-amber-100 bg-amber-50/50 px-2 py-1.5 text-xs"
              >
                <HourglassIcon className="size-3 text-amber-600 shrink-0" />
                <span className="font-medium text-amber-900 truncate min-w-0 flex-1">{item.jobId}</span>
                <span className="text-amber-700/80 shrink-0">{describeWaitingReason(item.waitingReason)}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}

function SessionSection({
  title,
  sessions,
  toProjectPath,
}: {
  title: string
  sessions: AgentSessionListItemDto[]
  toProjectPath: (path: string) => string
}) {
  if (sessions.length === 0) return null
  return (
    <div className="space-y-1">
      <h4 className="text-xs font-medium text-muted-foreground uppercase tracking-wider px-1">{title}</h4>
      {sessions.map((s) => (
        <a
          key={s.sessionId}
          href={toProjectPath(`/agent-sessions/${encodeURIComponent(s.sessionId)}`)}
          data-testid={`session-row-${s.sessionId}`}
          className="flex items-center gap-3 px-3 py-2 rounded-md hover:bg-muted/50 transition-colors text-sm"
        >
          {statusIcon(s.activity ?? 'unknown')}
          <span className="text-xs text-foreground font-medium truncate min-w-0 flex-1">
            {s.agentName}
          </span>
          <span className="text-xs text-muted-foreground shrink-0">
            {s.resolvedModel ?? 'unknown'}
          </span>
          <span className="text-[10px] text-muted-foreground/60 shrink-0">
            {formatTime(s.lastActivityAt ?? s.createdAt)}
          </span>
        </a>
      ))}
    </div>
  )
}

export function AgentDetailPage({
  components,
  dataHook = useDefaultData,
}: {
  components?: Partial<AgentDetailPageComponents>
  dataHook?: AgentDetailPageDataHook
} = {}) {
  const { AgentProfileEditor, SubscriptionsSection } = { ...defaultComponents, ...components }
  const { agentId } = useParams<{ agentId: string }>()
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const {
    agent,
    isLoading,
    isError,
    sessions: allSessions,
    sessionsLoading,
    archiveAgent,
    unarchiveAgent,
    detailStatus,
    detailStatusLoading,
  } = dataHook(agentId ?? '')
  const [editorOpen, setEditorOpen] = useState(false)
  const [archiveConfirmOpen, setArchiveConfirmOpen] = useState(false)

  useDocumentTitle(agent ? `${agent.name} — Mohist` : 'Agent — Mohist')

  const { model, variant } = useMemo(() => readAgentModelAndVariant(agent), [agent])
  const isArchived = agent?.status === 'archived'
  const readiness = agent?.readiness
  const readinessConclusion = readiness?.conclusion ?? 'Unknown'
  const isNeedsSetup = readinessConclusion === 'Needs setup'
  const isUnknownReadiness = readinessConclusion === 'Unknown'
  const launchBlockedByReadiness = isNeedsSetup

  const runningSessions = useMemo(
    () => allSessions.filter((s) => s.activity === 'active'),
    [allSessions],
  )
  const failedSessions = useMemo(
    () => allSessions.filter((s) => s.activity === 'unknown'),
    [allSessions],
  )
  const endedSessions = useMemo(
    () => allSessions.filter((s) => s.activity === 'idle'),
    [allSessions],
  )
  const recentSessions = useMemo(
    () =>
      [...allSessions]
        .sort((a, b) => {
          const aTime = a.lastActivityAt ?? a.createdAt
          const bTime = b.lastActivityAt ?? b.createdAt
          return new Date(bTime).getTime() - new Date(aTime).getTime()
        })
        .slice(0, 5),
    [allSessions],
  )

  function handleArchive() {
    if (!agent) return
    archiveAgent.mutate(agent.id, {
      onSuccess: () => {
        setArchiveConfirmOpen(false)
      },
    })
  }

  function handleUnarchive() {
    if (!agent) return
    unarchiveAgent.mutate(agent.id)
  }

  if (isLoading) {
    return (
      <div className="flex-1 flex items-center justify-center">
        <div className="text-sm text-muted-foreground">Loading agent...</div>
      </div>
    )
  }

  if (isError || !agent) {
    return (
      <div className="flex-1 flex items-center justify-center">
        <div className="text-sm text-red-500">Failed to load agent.</div>
      </div>
    )
  }

  return (
    <div
      data-testid="agent-detail-page"
      data-agent-id={agent.id}
      className="flex-1 overflow-y-auto bg-background"
    >
      <div className="max-w-4xl mx-auto px-6 py-6 space-y-6">
        <div className="flex items-start justify-between gap-4">
          <div className="flex items-center gap-4 min-w-0">
            <div className={`flex items-center justify-center size-12 rounded-xl shrink-0 ${isArchived ? 'bg-muted' : 'bg-blue-50'}`}>
              <BotIcon className={`size-6 ${isArchived ? 'text-muted-foreground' : 'text-blue-600'}`} />
            </div>
            <div className="min-w-0">
              <div className="flex items-center gap-2">
                <h1 className="text-lg font-semibold text-foreground truncate">{agent.name}</h1>
                {isArchived && (
                  <Badge variant="outline" className="text-[10px] px-1.5 py-0 h-4 text-muted-foreground border-muted-foreground/30">
                    <ArchiveIcon className="size-3 mr-0.5" />
                    Archived
                  </Badge>
                )}
              </div>
              <p className="text-xs text-muted-foreground mt-0.5">
                {model ? `Model · ${model}` : 'Model · Default'}
                {variant && ` · ${variant}`}
              </p>
            </div>
          </div>
          <div className="flex items-center gap-2 shrink-0">
            <Button
              variant="outline"
              size="sm"
              onClick={() => setEditorOpen(true)}
              data-testid="agent-detail-edit"
            >
              <PencilIcon />
              Edit
            </Button>
            {!isArchived ? (
              <Button
                size="sm"
                onClick={() => navigate(toProjectPath(`/agent-sessions/new?agent=${encodeURIComponent(agent.id)}`))}
                data-testid="agent-detail-new-session"
                disabled={launchBlockedByReadiness}
                title={launchBlockedByReadiness ? 'Readiness is Needs setup — fix the gaps first.' : undefined}
              >
                <PlayIcon />
                New Session
              </Button>
            ) : (
              <Button
                size="sm"
                disabled
                variant="outline"
                data-testid="agent-detail-new-session"
                className="opacity-50 cursor-not-allowed"
              >
                <PlayIcon />
                New Session
              </Button>
            )}
          </div>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
          <div className="md:col-span-2 space-y-6">
            <ReadinessCard readiness={readiness ?? undefined} toProjectPath={toProjectPath} />

            {!launchBlockedByReadiness && isUnknownReadiness && (
              <p
                data-testid="agent-detail-unknown-launch-hint"
                className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800"
              >
                Readiness is <span className="font-semibold">Unknown</span> — the launch will proceed and will wait for the server to validate execution.
              </p>
            )}

            <div className="rounded-lg border border-border bg-card p-4">
              <h3 className="text-sm font-medium text-foreground mb-3">Instructions</h3>
              <div
                data-testid="agent-detail-instructions"
                className="text-xs text-muted-foreground whitespace-pre-wrap leading-relaxed"
              >
                {agent.instructions || (
                  <span className="italic text-muted-foreground/50">No instructions set</span>
                )}
              </div>
            </div>

            <div className="rounded-lg border border-border bg-card p-4">
              <h3 className="text-sm font-medium text-foreground mb-3">Sessions</h3>
              {sessionsLoading ? (
                <div className="text-xs text-muted-foreground py-4 text-center">Loading sessions...</div>
              ) : allSessions.length === 0 ? (
                <div className="text-xs text-muted-foreground py-4 text-center">
                  No sessions yet. Start a new session to get started.
                </div>
              ) : (
                <div className="space-y-4" data-testid="agent-detail-sessions">
                  {runningSessions.length > 0 && (
                    <SessionSection title="Running" sessions={runningSessions} toProjectPath={toProjectPath} />
                  )}
                  {failedSessions.length > 0 && (
                    <SessionSection title="Failed" sessions={failedSessions} toProjectPath={toProjectPath} />
                  )}
                  {endedSessions.length > 0 && (
                    <SessionSection title="Ended" sessions={endedSessions} toProjectPath={toProjectPath} />
                  )}
                  {recentSessions.length > 0 && (
                    <SessionSection title="Recent" sessions={recentSessions} toProjectPath={toProjectPath} />
                  )}
                </div>
              )}
            </div>
          </div>

          <div className="space-y-4">
            <AvailabilityCard
              availability={detailStatus?.availability}
              waitingWork={detailStatus?.waitingWork ?? []}
              loading={detailStatusLoading}
            />

            <div className="rounded-lg border border-border bg-card p-4">
              <h3 className="text-sm font-medium text-foreground mb-3">Agent Config</h3>
              <div className="space-y-2" data-testid="agent-detail-config">
                <div className="flex justify-between items-center">
                  <span className="text-xs text-muted-foreground">Model</span>
                  <span className="text-xs font-medium text-foreground">{model ?? 'Default'}</span>
                </div>
                <div className="flex justify-between items-center">
                  <span className="text-xs text-muted-foreground">Variant</span>
                  <span className="text-xs font-medium text-foreground">{variant ?? 'Default'}</span>
                </div>
              </div>
            </div>

            <div className="rounded-lg border border-border bg-card p-4">
              <h3 className="text-sm font-medium text-foreground mb-3">Skills</h3>
              {agent.skills && agent.skills.length > 0 ? (
                <div className="flex flex-wrap gap-1.5" data-testid="agent-detail-skills">
                  {agent.skills.map((skill) => (
                    <Badge key={skill} variant="secondary" className="text-[10px] px-1.5 py-0 h-4">
                      {skill}
                    </Badge>
                  ))}
                </div>
              ) : (
                <span className="text-xs text-muted-foreground/50 italic">No skills configured</span>
              )}
            </div>

            <SubscriptionsSection agent={agent} />

            <div className="rounded-lg border border-border bg-card p-4">
              <h3 className="text-sm font-medium text-foreground mb-3">Actions</h3>
              <div className="space-y-2">
                {!isArchived ? (
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => setArchiveConfirmOpen(true)}
                    className="w-full justify-start text-red-600 hover:text-red-700 hover:bg-red-50"
                    data-testid="agent-detail-archive-btn"
                    disabled={archiveAgent.isPending}
                  >
                    <ArchiveIcon />
                    Archive
                  </Button>
                ) : (
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={handleUnarchive}
                    className="w-full justify-start"
                    data-testid="agent-detail-unarchive-btn"
                    disabled={unarchiveAgent.isPending}
                  >
                    {unarchiveAgent.isPending ? (
                      <Loader2Icon className="size-4 animate-spin" />
                    ) : (
                      <RotateCcwIcon />
                    )}
                    Unarchive
                  </Button>
                )}
              </div>
            </div>
          </div>
        </div>
      </div>

      {editorOpen && (
        <AgentProfileEditor
          agent={agent}
          open={editorOpen}
          onClose={() => setEditorOpen(false)}
          onSaved={() => {
            setEditorOpen(false)
          }}
        />
      )}

      <Dialog open={archiveConfirmOpen} onOpenChange={setArchiveConfirmOpen}>
        <DialogContent className="sm:max-w-sm" data-testid="agent-detail-archive-confirm-dialog">
          <DialogHeader>
            <DialogTitle>Archive Agent</DialogTitle>
            <DialogDescription>
              This agent will leave the Active group and will not be launchable for new sessions.
              It remains visible in the Archived group and can be restored from this page.
            </DialogDescription>
          </DialogHeader>
          <div className="flex justify-end gap-2 pt-2">
            <Button
              variant="outline"
              onClick={() => setArchiveConfirmOpen(false)}
              disabled={archiveAgent.isPending}
              data-testid="agent-detail-archive-cancel"
            >
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={handleArchive}
              disabled={archiveAgent.isPending}
              data-testid="agent-detail-archive-confirm"
            >
              {archiveAgent.isPending && <Loader2Icon className="size-4 animate-spin" />}
              Archive
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}
