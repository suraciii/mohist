import { useMemo, useState, type ComponentProps, type ComponentType } from 'react'
import { useNavigate } from 'react-router-dom'
import { PlusIcon, BotIcon, ArchiveIcon, CircleIcon } from 'lucide-react'
import { useAgents, readAgentModelAndVariant } from '../../../entities/agent'
import type { AgentInfo } from '../../../entities/agent'
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

function getAvailabilityStatus(agent: AgentInfo): { label: string; dotClass: string } {
  if (agent.status === 'archived') {
    return { label: 'Archived', dotClass: 'bg-gray-400' }
  }
  return { label: 'Active', dotClass: 'bg-emerald-500' }
}

function AgentRow({ agent }: { agent: AgentInfo }) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { model, variant } = useMemo(() => readAgentModelAndVariant(agent), [agent])
  const agentType = useMemo(() => getAgentType(agent), [agent])
  const availability = useMemo(() => getAvailabilityStatus(agent), [agent])
  const isArchived = agent.status === 'archived'

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
      className={`flex items-center gap-4 px-4 py-3 cursor-pointer transition-colors hover:bg-muted/50 ${
        isArchived ? 'opacity-60' : ''
      }`}
    >
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
        </div>

        <div className="flex items-center gap-1.5 shrink-0">
          <CircleIcon className={`size-2 ${availability.dotClass}`} />
          <span className="text-xs text-muted-foreground">{availability.label}</span>
        </div>
      </div>
    </div>
  )
}

function AgentEmptyState({ onCreateClick }: { onCreateClick: () => void }) {
  return (
    <div
      data-testid="agents-empty-state"
      className="rounded-lg border border-dashed border-border bg-muted/30 px-4 py-16 text-center"
    >
      <BotIcon className="size-10 mx-auto text-muted-foreground/40 mb-3" />
      <p className="text-sm font-medium text-foreground mb-1">No agents defined</p>
      <p className="text-xs text-muted-foreground mb-4 max-w-sm mx-auto">
        Agent profiles let you configure an instruction set, model, and skills for direct agent sessions
        outside of issue workflows.
      </p>
      <Button onClick={onCreateClick} data-testid="agents-empty-create">
        <PlusIcon />
        Create Agent
      </Button>
    </div>
  )
}

export function AgentListPage({
  components,
}: {
  components?: Partial<AgentListPageComponents>
} = {}) {
  const { AgentProfileEditor } = { ...defaultComponents, ...components }
  useDocumentTitle('Agents — Mohist')
  const { data: agents, isLoading } = useAgents()
  const [editorOpen, setEditorOpen] = useState(false)

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
    <div
      data-testid="agent-list-page"
      className="flex-1 overflow-y-auto bg-background"
    >
      <div className="max-w-4xl mx-auto px-6 py-6 space-y-5">
        <div className="flex items-center justify-between gap-3 flex-wrap">
          <div>
            <h1 className="text-lg font-semibold text-foreground">Agents</h1>
            <p className="text-xs text-muted-foreground mt-0.5">
              Manage agent profiles and start direct sessions.
            </p>
          </div>
          <Button
            onClick={() => setEditorOpen(true)}
            data-testid="agent-list-create"
          >
            <PlusIcon />
            New Agent
          </Button>
        </div>

        {!hasAgents ? (
          <AgentEmptyState
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
                  <AgentRow key={agent.id} agent={agent} />
                ))}
              </div>
            )}
            {archivedAgents.length > 0 && (
              <div data-testid="archived-section">
                <div className="px-4 py-2 text-xs font-medium text-muted-foreground uppercase tracking-wider bg-muted/50 border-b border-border">
                  Archived ({archivedAgents.length})
                </div>
                {archivedAgents.map((agent) => (
                  <AgentRow key={agent.id} agent={agent} />
                ))}
              </div>
            )}
          </div>
        )}
      </div>

      {editorOpen && (
        <AgentProfileEditor
          agent={null}
          open={editorOpen}
          onClose={() => setEditorOpen(false)}
        />
      )}
    </div>
  )
}
