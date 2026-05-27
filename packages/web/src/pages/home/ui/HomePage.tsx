import { useState } from 'react'
import { useIssues, useArchivedIssues } from '../../../entities/issue'
import { useProjects, useProject } from '../../../entities/project'
import { useAgentStatus } from '../../../entities/agent'
import { KanbanBoard } from '../../../widgets/kanban-board'
import { CreateProjectDialog } from '../../../widgets/create-project-dialog'
import { Button } from '../../../shared/ui/components/button'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

export function HomePage() {
  const { projectId } = useProject()
  const { data: projects, isLoading: projectsLoading } = useProjects()
  const { data: issues, isLoading } = useIssues(projectId ? { projectId } : undefined)
  const { data: archivedIssues } = useArchivedIssues(projectId ? { projectId } : undefined)
  const { data: agentStatus } = useAgentStatus()
  const [showCreateProject, setShowCreateProject] = useState(false)

  useDocumentTitle('Mohist', agentStatus?.running ?? false)

  if (projectsLoading) {
    return null
  }

  if (projects && projects.length === 0) {
    return (
      <>
        <div className="flex items-center justify-center flex-1">
          <div className="text-center">
            <div className="text-muted-foreground text-lg mb-4">No projects yet</div>
            <Button onClick={() => setShowCreateProject(true)}>Create Project</Button>
          </div>
        </div>
        <CreateProjectDialog open={showCreateProject} onClose={() => setShowCreateProject(false)} />
      </>
    )
  }

  return isLoading ? (
    <div className="flex items-center justify-center flex-1">
      <div className="text-muted-foreground">Loading...</div>
    </div>
  ) : (
    <KanbanBoard
      issues={issues ?? []}
      agentStatus={agentStatus ?? { running: false, issueId: null, issueNumber: null, activeAgents: [], capacity: { active: 0, max: 8 } }}
      archivedCount={archivedIssues?.length ?? 0}
    />
  )
}
