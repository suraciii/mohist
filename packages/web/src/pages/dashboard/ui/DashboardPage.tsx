import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useProjects, useProject, useProjectPath } from '../../../entities/project'
import { useAgentStatus } from '../../../entities/agent'
import { CreateProjectDialog } from '../../../widgets/create-project-dialog'
import { DashboardDigestWidget } from '../../../widgets/dashboard-digest'
import { PulseZone } from '../../../widgets/dashboard-pulse'
import { FactoryStatusHeadline } from '../../../widgets/factory-status'
import { AttentionHero } from '../../../widgets/attention-hero'
import { Button } from '../../../shared/ui/components/button'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { BotIcon } from 'lucide-react'
import { ProductivityZone } from '../productivity/ProductivityZone'
import { DashboardZone } from './DashboardZone'
import type { DashboardZoneId } from './DashboardZone'

const DASHBOARD_ZONES: { id: DashboardZoneId; name: string }[] = [
  { id: 'pulse', name: 'Pulse' },
  { id: 'productivity', name: 'Productivity' },
  { id: 'digest', name: 'Digest' },
]

export function DashboardPage() {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
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
      <div className="flex flex-col gap-4 md:gap-6">
        <div data-testid="dashboard-headline">
          <FactoryStatusHeadline />
        </div>
        <div data-testid="dashboard-hero">
          <AttentionHero />
          <div className="mt-4 flex justify-end">
            <Button
              variant="outline"
              onClick={() => navigate(toProjectPath('/agent-sessions/new'))}
              data-testid="ask-agent-project"
            >
              <BotIcon className="size-4 mr-2" />
              Ask Agent
            </Button>
          </div>
        </div>
        <div
          data-testid="dashboard-zones"
          className="grid gap-4 md:gap-6 grid-cols-1 md:grid-cols-2"
        >
          {DASHBOARD_ZONES.map((zone) =>
            zone.id === 'digest' ? (
              <DashboardZone key={zone.id} id={zone.id} name={zone.name}>
                <DashboardDigestWidget />
              </DashboardZone>
            ) : zone.id === 'pulse' ? (
              <DashboardZone key={zone.id} id={zone.id} name={zone.name}>
                <PulseZone />
              </DashboardZone>
            ) : (
              <DashboardZone key={zone.id} id={zone.id} name={zone.name}>
                <ProductivityZone />
              </DashboardZone>
            ),
          )}
        </div>
      </div>
    </div>
  )
}
