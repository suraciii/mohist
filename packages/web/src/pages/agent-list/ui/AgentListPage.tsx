import { useMemo, useState, type ComponentProps, type ComponentType } from 'react'
import { useNavigate } from 'react-router-dom'
import { PlusIcon, BotIcon, ArchiveIcon, CircleIcon, PlayIcon } from 'lucide-react'
import {
  getAgentAvailabilityFeedback,
  useAgentListAvailability,
  useAgents,
  readAgentModelAndVariant,
} from '../../../entities/agent'
import type { AgentAvailabilitySummaryEntry, AgentInfo } from '../../../entities/agent'
import { useProjectPath } from '../../../entities/project'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { Button } from '@/shared/ui/components/button'
import { Badge } from '@/shared/ui/components/badge'
import { AgentProfileEditor as DefaultAgentProfileEditor } from '../../../widgets/agent-profile-editor'

export interface AgentListPageComponents {
  AgentProfileEditor: ComponentType<ComponentProps<typeof DefaultAgentProfileEditor>>
}

const defaultComponents: AgentListPageComponents = {
  AgentProfileEditor: DefaultAgentProfileEditor,
}

function getAgentType(agent: AgentInfo): string {
  const config = agent.agentConfig
  if (config && typeof config === 'object' && 'type' in config) {
    return String(config.type)
  }
  return 'opencode'
}

function getLifecycleStatus(agent: AgentInfo): { label: string; dotClass: string } {
  if (agent.status === 'archived') {
    return { label: 'Archived', dotClass: 'bg-gray-400' }
  }
  return { label: 'Active', dotClass: 'bg-emerald-500' }
}

function AgentRow({
  agent,
  availability,
  availabilityLoading,
}: {
  agent: AgentInfo
  availability?: AgentAvailabilitySummaryEntry
  availabilityLoading: boolean
}) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { model, variant } = useMemo(() => readAgentModelAndVariant(agent), [agent])
  const agentType = useMemo(() => getAgentType(agent), [agent])
  const lifecycle = useMemo(() => getLifecycleStatus(agent), [agent])
  const isArchived = agent.status === 'archived'
  const executability = agent.executability?.state ?? 'unknown'
  const leadingGap = agent.executability?.gaps[0]
  const availabilityFeedback =
    !isArchived && availability && !availability.canStartNow
      ? getAgentAvailabilityFeedback(availability.waitingReason)
      : null

  return (
    <div
      role="button"
      tabIndex={0}
      data-testid={`agent-row-${agent.id}`}
      data-status={agent.status}
      onClick={() => navigate(toProjectPath(`/agents/${encodeURIComponent(agent.id)}`))}
      onKeyDown={(e) => {
        if (e.key === 'Enter') navigate(toProjectPath(`/agents/${encodeURIComponent(agent.id)}`))
      }}
      className={`px-4 py-3 cursor-pointer transition-colors hover:bg-muted/50 ${isArchived ? 'opacity-60' : ''}`}
    >
      <div className="flex items-center gap-4">
        <div className="flex items-center justify-center size-10 rounded-lg bg-muted shrink-0">
          <BotIcon className={`size-5 ${isArchived ? 'text-muted-foreground' : 'text-blue-600'}`} />
        </div>

        <div className="flex min-w-0 flex-1 items-center gap-4">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium text-foreground truncate">{agent.name}</span>
              {isArchived && (
                <Badge variant="outline" className="text-[10px] px-1 py-0 h-4 text-muted-foreground">
                  <ArchiveIcon className="size-3 mr-0.5" />
                  Archived
                </Badge>
              )}
            </div>
            <div className="flex items-center gap-2 mt-0.5">
              <span className="text-xs text-muted-foreground">{agentType}</span>
              {model && (
                <>
                  <span className="text-xs text-muted-foreground/50">·</span>
                  <span className="text-xs text-muted-foreground">{model}</span>
                  {variant && (
                    <>
                      <span className="text-xs text-muted-foreground/50">·</span>
                      <span className="text-xs text-muted-foreground">{variant}</span>
                    </>
                  )}
                </>
              )}
            </div>
            <p data-testid={`agent-purpose-${agent.id}`} className="mt-2 text-xs text-muted-foreground">
              {agent.purpose?.trim() || 'No purpose set'}
            </p>
          </div>

          <div className="flex items-center gap-1.5 shrink-0">
            <CircleIcon className={`size-2 ${lifecycle.dotClass}`} />
            <span className="text-xs text-muted-foreground">{lifecycle.label}</span>
          </div>
        </div>
      </div>

      <div className="mt-3 grid grid-cols-1 gap-1.5 border-t border-border/60 pt-3 text-xs sm:grid-cols-3">
        <span
          data-testid={`agent-executability-${agent.id}`}
          data-state={executability}
          className={
            executability === 'executable'
              ? 'text-emerald-700'
              : executability === 'unknown'
                ? 'text-muted-foreground'
                : 'text-amber-700'
          }
        >
          Executability: {executability}
        </span>
        {leadingGap && (
          <p data-testid={`agent-executability-guidance-${agent.id}`} className="text-muted-foreground">
            {leadingGap.nextAction}
          </p>
        )}
        <div>
          <span
            data-testid={`agent-availability-${agent.id}`}
            data-state={
              isArchived ? 'archived' : availability ? (availability.canStartNow ? 'available' : 'waiting') : 'unknown'
            }
            className={availability?.canStartNow ? 'text-emerald-700' : 'text-muted-foreground'}
          >
            {isArchived
              ? 'Availability: Not tracked for archived agents'
              : availability
                ? availability.canStartNow
                  ? 'Availability: Can start now'
                  : `Availability: ${availabilityFeedback!.title}`
                : availabilityLoading
                  ? 'Availability: Loading...'
                  : 'Availability: Unknown'}
          </span>
          {availabilityFeedback && (
            <p
              data-testid={`agent-availability-guidance-${agent.id}`}
              data-feedback-kind={availabilityFeedback.kind}
              className="mt-1 text-muted-foreground"
            >
              {availabilityFeedback.message} {availabilityFeedback.nextAction}
            </p>
          )}
        </div>
        <span data-testid={`agent-workload-${agent.id}`} className="text-muted-foreground">
          {isArchived
            ? 'Workload: Not tracked'
            : `Active: ${availability?.activeRuns ?? 'unknown'}, Queued: ${availability?.queuedCount ?? 'unknown'}`}
        </span>
      </div>
    </div>
  )
}

function AgentEmptyState({
  onStartTask,
  onCreateClick,
}: {
  onStartTask: () => void
  onCreateClick: () => void
}) {
  return (
    <div
      data-testid="agents-empty-state"
      className="rounded-lg border border-dashed border-border bg-muted/30 px-4 py-16 text-center"
    >
      <BotIcon className="size-10 mx-auto text-muted-foreground/40 mb-3" />
      <p className="text-sm font-medium text-foreground mb-1">No agents defined</p>
      <p className="text-xs text-muted-foreground mb-4 max-w-sm mx-auto">
        Start with the work you need done. Mohist will create an Agent for the task, or you can configure a profile first.
      </p>
      <div className="flex flex-wrap justify-center gap-2">
        <Button onClick={onStartTask} data-testid="agents-empty-task">
          <PlayIcon />
          Start with a task
        </Button>
        <Button variant="outline" onClick={onCreateClick} data-testid="agents-empty-create">
          <PlusIcon />
          Configure an Agent
        </Button>
      </div>
    </div>
  )
}

export function AgentListPage({ components }: { components?: Partial<AgentListPageComponents> } = {}) {
  const { AgentProfileEditor } = { ...defaultComponents, ...components }
  useDocumentTitle('Agents — Mohist')
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { data: agents, isLoading } = useAgents()
  const { data: availability, isLoading: availabilityLoading } = useAgentListAvailability()
  const [editorOpen, setEditorOpen] = useState(false)
  const availabilityByAgentId = useMemo(
    () => new Map((availability ?? []).map((entry) => [entry.agentId, entry])),
    [availability],
  )

  if (isLoading) {
    return (
      <div className="flex-1 flex items-center justify-center">
        <div className="text-sm text-muted-foreground">Loading agents...</div>
      </div>
    )
  }

  const activeAgents = agents?.filter((a) => a.status !== 'archived') ?? []
  const archivedAgents = agents?.filter((a) => a.status === 'archived') ?? []
  const hasAgents = agents && agents.length > 0

  return (
    <div data-testid="agent-list-page" className="flex-1 overflow-y-auto bg-background">
      <div className="max-w-4xl mx-auto px-6 py-6 space-y-5">
        <div className="flex items-center justify-between gap-3 flex-wrap">
          <div>
            <h1 className="text-lg font-semibold text-foreground">Agents</h1>
            <p className="text-xs text-muted-foreground mt-0.5">Manage agent profiles and start direct sessions.</p>
          </div>
          <Button onClick={() => setEditorOpen(true)} data-testid="agent-list-create">
            <PlusIcon />
            New Agent
          </Button>
        </div>

        {!hasAgents ? (
          <AgentEmptyState
            onStartTask={() => navigate(toProjectPath('/agent-sessions/new'))}
            onCreateClick={() => setEditorOpen(true)}
          />
        ) : (
          <div className="rounded-lg border border-border bg-card overflow-hidden" data-testid="agent-list">
            {activeAgents.length > 0 && (
              <div data-testid="active-section">
                <div className="px-4 py-2 text-xs font-medium text-muted-foreground uppercase tracking-wider bg-muted/50 border-b border-border">
                  Active ({activeAgents.length})
                </div>
                {activeAgents.map((agent) => (
                  <AgentRow
                    key={agent.id}
                    agent={agent}
                    availability={availabilityByAgentId.get(agent.id)}
                    availabilityLoading={availabilityLoading}
                  />
                ))}
              </div>
            )}
            {archivedAgents.length > 0 && (
              <div data-testid="archived-section">
                <div className="px-4 py-2 text-xs font-medium text-muted-foreground uppercase tracking-wider bg-muted/50 border-b border-border">
                  Archived ({archivedAgents.length})
                </div>
                {archivedAgents.map((agent) => (
                  <AgentRow
                    key={agent.id}
                    agent={agent}
                    availability={availabilityByAgentId.get(agent.id)}
                    availabilityLoading={availabilityLoading}
                  />
                ))}
              </div>
            )}
          </div>
        )}
      </div>

      {editorOpen && <AgentProfileEditor agent={null} open={editorOpen} onClose={() => setEditorOpen(false)} />}
    </div>
  )
}
