import { useState } from 'react'
import { useProjects, useProject } from '../../../entities/project'
import { useAgentStatus } from '../../../entities/agent'
import { CreateProjectDialog } from '../../../widgets/create-project-dialog'
import { Button } from '../../../shared/ui/components/button'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { DashboardZonePlaceholder } from './DashboardZonePlaceholder'
import type { DashboardZoneId } from './DashboardZonePlaceholder'

const DASHBOARD_ZONES: { id: DashboardZoneId; name: string }[] = [
  { id: 'attention', name: 'Attention' },
  { id: 'pulse', name: 'Pulse' },
  { id: 'productivity', name: 'Productivity' },
  { id: 'digest', name: 'Digest' },
]

export function DashboardPage() {
  const { data: projects, isLoading: projectsLoading } = useProjects()
  const { currentProject } = useProject()
  const { data: agentStatus } = useAgentStatus()
  const [showCreateProject, setShowCreateProject] = useState(false)

  useDocumentTitle('Dashboard — Mohist', agentStatus?.running ?? false)

  if (projectsLoading) {
    return null
  }

  if (!projects || projects.length === 0) {
    return (
      <>
        <div
          data-testid="dashboard-empty-state"
          className="flex items-center justify-center flex-1"
        >
          <div className="text-center">
            <div className="text-muted-foreground text-lg mb-4">
              No projects yet
            </div>
            <Button
              onClick={() => setShowCreateProject(true)}
              data-testid="dashboard-create-project"
            >
              Create Project
            </Button>
          </div>
        </div>
        <CreateProjectDialog
          open={showCreateProject}
          onClose={() => setShowCreateProject(false)}
        />
      </>
    )
  }

  return (
    <div
      data-testid="dashboard-page"
      data-project={currentProject?.name ?? ''}
      className="flex-1 overflow-y-auto p-4 md:p-6"
    >
      <div
        data-testid="dashboard-zones"
        className="grid gap-4 md:gap-6 grid-cols-1 md:grid-cols-2"
      >
        {DASHBOARD_ZONES.map((zone) => (
          <DashboardZonePlaceholder key={zone.id} id={zone.id} name={zone.name} />
        ))}
      </div>
    </div>
  )
}
