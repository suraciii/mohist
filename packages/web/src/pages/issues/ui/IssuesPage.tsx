import type { ComponentProps, ComponentType } from 'react'
import { useIssues, useArchivedIssues } from '../../../entities/issue'
import { useProject } from '../../../entities/project'
import { useAgentStatus } from '../../../entities/agent'
import { KanbanBoard as DefaultKanbanBoard } from '../../../widgets/kanban-board'

export interface IssuesPageComponents {
  KanbanBoard: ComponentType<ComponentProps<typeof DefaultKanbanBoard>>
}

const defaultComponents: IssuesPageComponents = {
  KanbanBoard: DefaultKanbanBoard,
}

export function IssuesPage({
  components,
}: {
  components?: Partial<IssuesPageComponents>
} = {}) {
  const { KanbanBoard } = { ...defaultComponents, ...components }
  const { projectId } = useProject()
  const { data: issues, isLoading } = useIssues(projectId ? { projectId } : undefined)
  const { data: archivedIssues } = useArchivedIssues(projectId ? { projectId } : undefined)
  const { data: agentStatus } = useAgentStatus()

  if (isLoading) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="text-muted-foreground">Loading...</div>
      </div>
    )
  }

  return (
    <KanbanBoard
      issues={issues ?? []}
      agentStatus={agentStatus ?? { running: false, issueNumber: null, activeAgents: [], capacity: { active: 0, max: 8 } }}
      archivedCount={archivedIssues?.length ?? 0}
    />
  )
}
