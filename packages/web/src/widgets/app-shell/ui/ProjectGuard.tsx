import { Outlet, useLocation } from 'react-router-dom'
import { useProjects } from '../../../entities/project/api/queries'
import { useProject } from '../../../entities/project/model/ProjectContext'
import { CreateProjectDialog } from '../../create-project-dialog/ui/CreateProjectDialog'
import { useEffect, useState } from 'react'

export function ProjectGuard() {
  const location = useLocation()
  const { projectId, setProjectId } = useProject()
  const { data: projects, isLoading } = useProjects()
  const [showCreateProject, setShowCreateProject] = useState(false)

  useEffect(() => {
    if (!projectId && projects && projects.length > 0) {
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
            <div className="text-gray-400 text-lg mb-4">No projects yet</div>
            <button
              onClick={() => setShowCreateProject(true)}
              className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 text-sm"
            >
              Create Project
            </button>
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
