import { Outlet, useLocation } from 'react-router-dom'
import { useProjects, useProject } from '../../../entities/project'
import { CreateProjectDialog } from '../../create-project-dialog/ui/CreateProjectDialog'
import { useEffect, useState } from 'react'
import { Button } from '@/shared/ui/components/button'

export function ProjectGuard() {
  const location = useLocation()
  const { projectId, setProjectId } = useProject()
  const { data: projects, isLoading } = useProjects()
  const [showCreateProject, setShowCreateProject] = useState(false)

  useEffect(() => {
    if (projects && projects.length > 0 && (!projectId || !projects.some((project) => project.id === projectId))) {
      setProjectId(projects[0].id)
    }
  }, [projectId, projects, setProjectId])

  if (location.pathname === '/settings' || location.pathname === '/logs') {
    return <Outlet />
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center flex-1">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" />
      </div>
    )
  }

  if (!projects || projects.length === 0) {
    return (
      <>
        <div className="flex items-center justify-center flex-1">
          <div className="text-center">
            <div className="text-muted-foreground text-lg mb-4">No projects yet</div>
            <Button
              onClick={() => setShowCreateProject(true)}
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

  return <Outlet />
}
