import { Link } from 'react-router-dom'
import { BotIcon } from 'lucide-react'
import { useAgentByName, readAgentModelAndVariant } from '../../../entities/agent'
import { useProjectPath } from '../../../entities/project'
import { parseProfileAgentNames, type WorkflowProfileDetail } from '../../../entities/settings'
import { Badge } from '@/shared/ui/components/badge'

function runtimeLabel(runtime: string) {
  return runtime === 'opencode' ? 'OpenCode' : 'Pi'
}

function AgentRow({ name }: { name: string }) {
  const toProjectPath = useProjectPath()
  const { data: agent, isLoading, isError } = useAgentByName(name)

  if (isLoading) {
    return (
      <li data-testid={`workflow-agent-${name}`} className="py-2 text-xs text-muted-foreground">
        Loading {name}…
      </li>
    )
  }

  if (isError || !agent) {
    return (
      <li data-testid={`workflow-agent-${name}`} data-state="missing" className="py-2 text-xs text-amber-700">
        {name} is not defined in this Project.
      </li>
    )
  }

  const { runtime, model, reasoningEffort, variant } = readAgentModelAndVariant(agent)
  const isBuiltIn = agent.origin === 'built-in'

  return (
    <li data-testid={`workflow-agent-${name}`} data-state="resolved" className="space-y-1 py-2">
      <div className="flex flex-wrap items-center gap-2">
        <BotIcon className="size-3.5 text-muted-foreground" />
        <span className="text-sm font-medium text-foreground">{agent.name}</span>
        <Badge variant="outline" className="text-[10px] px-1.5 py-0 h-4">
          {isBuiltIn ? 'Built-in' : 'Project'}
        </Badge>
        <Link
          to={toProjectPath(`/agents/${encodeURIComponent(agent.id)}`)}
          data-testid={`workflow-agent-configure-${name}`}
          className="text-xs font-medium text-primary underline underline-offset-2"
        >
          Configure
        </Link>
      </div>
      <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
        <span data-testid={`workflow-agent-runtime-${name}`}>{runtimeLabel(runtime)}</span>
        <span aria-hidden="true">·</span>
        <span data-testid={`workflow-agent-model-${name}`}>{model ?? 'Runtime default'}</span>
        <span aria-hidden="true">·</span>
        <span data-testid={`workflow-agent-effort-${name}`}>
          Reasoning Effort: {reasoningEffort ?? 'Runtime behavior'}
        </span>
        <span aria-hidden="true">·</span>
        <span data-testid={`workflow-agent-variant-${name}`}>Variant: {variant ?? 'None'}</span>
      </div>
    </li>
  )
}

/**
 * Read-only list of the named Agents a Workflow Profile Definition executes
 * through `mohist/agent` tasks, with each Agent's effective configuration and
 * a route to the Agents page. The Agents page is the only configuration
 * surface; this block adds no selector.
 */
export function WorkflowAgentsBlock({
  profile,
  isLoading = false,
  emptyMessage = 'This Profile does not reference a named Agent.',
}: {
  profile: WorkflowProfileDetail | undefined
  isLoading?: boolean
  emptyMessage?: string
}) {
  if (isLoading && !profile) {
    return (
      <div data-testid="workflow-agents-block" data-state="loading" className="text-xs text-muted-foreground">
        Loading named Agents…
      </div>
    )
  }

  const names = parseProfileAgentNames(profile?.yaml)
  if (names.length === 0) {
    return (
      <div data-testid="workflow-agents-block" data-state="empty" className="text-xs text-muted-foreground">
        {emptyMessage}
      </div>
    )
  }

  return (
    <div data-testid="workflow-agents-block" data-state="resolved">
      <ul className="divide-y divide-border/60" data-testid="workflow-agents-list">
        {names.map((name) => (
          <AgentRow key={name} name={name} />
        ))}
      </ul>
    </div>
  )
}
