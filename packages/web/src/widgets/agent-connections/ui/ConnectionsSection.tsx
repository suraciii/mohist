import { useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import { Link2Icon, Loader2Icon, PlusIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { useProjectPath } from '../../../entities/project'
import type { AgentInfo } from '../../../entities/agent'
import {
  useAgentConnections,
  useCreateAgentConnection,
} from '../../../entities/agent-connection'
import type { AgentConnectionDto } from '../../../entities/agent-connection'

interface Props {
  agent: Pick<AgentInfo, 'id' | 'status'>
  operationsHook?: ConnectionOperationsHook
}

export interface ConnectionOperations {
  connectionsQuery: Pick<ReturnType<typeof useAgentConnections>, 'data' | 'isLoading'>
  createMutation: Pick<ReturnType<typeof useCreateAgentConnection>, 'mutate' | 'isPending'>
}

export type ConnectionOperationsHook = (agentRef: string) => ConnectionOperations

const useDefaultOperations: ConnectionOperationsHook = () => ({
  connectionsQuery: useAgentConnections(),
  createMutation: useCreateAgentConnection(),
})

function label(value: string | null | undefined): string {
  if (!value) return 'Unknown'
  return value.replaceAll('_', ' ')
}

function describeConnectionState(connection: AgentConnectionDto): { label: string; tone: 'muted' | 'amber' | 'emerald' } {
  if (connection.setupProgress !== 'complete') {
    return { label: 'setup incomplete', tone: 'amber' }
  }
  if (connection.connectionHealth !== 'healthy') {
    return { label: 'unhealthy', tone: 'amber' }
  }
  if (connection.desiredState === 'disabled') {
    return { label: 'disabled', tone: 'muted' }
  }
  return { label: 'ready', tone: 'emerald' }
}

function StateBadge({ tone, label: text }: { tone: 'muted' | 'amber' | 'emerald'; label: string }) {
  const className =
    tone === 'emerald'
      ? 'text-[10px] px-1.5 py-0 h-4 text-emerald-700 border-emerald-300'
      : tone === 'amber'
        ? 'text-[10px] px-1.5 py-0 h-4 text-amber-700 border-amber-300'
        : 'text-[10px] px-1.5 py-0 h-4 text-muted-foreground border-muted-foreground/30'
  return (
    <span
      data-state={tone}
      className={`inline-flex items-center rounded border bg-background ${className}`}
    >
      {text}
    </span>
  )
}

export function ConnectionsSection({ agent, operationsHook = useDefaultOperations }: Props) {
  const isArchived = agent.status === 'archived'
  const { connectionsQuery, createMutation } = operationsHook(agent.id)
  const { data: allConnections = [], isLoading } = connectionsQuery
  const toProjectPath = useProjectPath()
  const navigate = useNavigate()

  const connections = useMemo(
    () => allConnections.filter((connection) => connection.agentId === agent.id),
    [allConnections, agent.id],
  )

  function handleAdd() {
    if (isArchived) return
    createMutation.mutate(
      { agentId: agent.id },
      {
        onSuccess: (created) => {
          navigate(toProjectPath(`/connections/${encodeURIComponent(created.connection.id)}`))
        },
      },
    )
  }

  return (
    <div className="rounded-lg border border-border bg-card p-4" data-testid="agent-connections-section">
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-sm font-medium text-foreground">Connections</h3>
        <Button
          size="sm"
          variant="outline"
          onClick={handleAdd}
          data-testid="agent-connections-add-slack"
          disabled={isArchived || createMutation.isPending}
          aria-label="Add Slack connection"
        >
          {createMutation.isPending ? <Loader2Icon className="size-4 animate-spin" /> : <PlusIcon />}
          Add Slack
        </Button>
      </div>

      {isArchived && (
        <div
          data-testid="agent-connections-archived-notice"
          className="rounded-md bg-muted/60 border border-border px-3 py-2 text-xs text-muted-foreground mb-3"
        >
          Archived agents cannot receive new Slack Connections. Their existing Connections are
          also inactive.
        </div>
      )}

      {isLoading ? (
        <div data-testid="agent-connections-loading" className="text-xs text-muted-foreground py-4 text-center">
          Loading connections...
        </div>
      ) : connections.length === 0 ? (
        <div
          data-testid="agent-connections-empty"
          className="text-xs text-muted-foreground py-4 text-center"
        >
          No Connections yet. Add Slack to start setup.
        </div>
      ) : (
        <ul className="space-y-2" data-testid="agent-connections-list">
          {connections.map((connection) => {
            const state = describeConnectionState(connection)
            return (
              <li
                key={connection.id}
                data-testid={`agent-connection-row-${connection.id}`}
                data-connection-state={state.tone}
                className="rounded-md border border-border bg-background/60 px-3 py-2"
              >
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2 flex-wrap">
                      <Link2Icon className="size-3 text-muted-foreground shrink-0" />
                      <a
                        href={toProjectPath(`/connections/${encodeURIComponent(connection.id)}`)}
                        data-testid={`agent-connection-row-${connection.id}-link`}
                        className="text-sm font-medium text-foreground hover:underline truncate"
                      >
                        {connection.id}
                      </a>
                      <StateBadge tone={state.tone} label={state.label} />
                    </div>
                    <div
                      className="text-[11px] font-mono text-muted-foreground mt-0.5"
                      data-testid={`agent-connection-row-${connection.id}-bot`}
                    >
                      bot: {connection.botName || '—'}
                    </div>
                  </div>
                </div>
                <div
                  className="mt-1 text-xs text-muted-foreground italic"
                  data-testid={`agent-connection-row-${connection.id}-setup`}
                >
                  Setup: {label(connection.setupProgress)}
                </div>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
