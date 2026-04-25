import { useEffect, useState } from 'react'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import { useIssues, useProjects, useCurrentProject, useAgentStatus } from './hooks/useQueries'
import useSSE from './hooks/useSSE'
import { ProjectProvider, useProject } from './context/ProjectContext'
import { KanbanBoard } from './components/KanbanBoard'
import { Header } from './components/Header'
import { IssueDetailPage } from './components/IssueDetailPage'
import { ExplorePage } from './components/ExplorePage'
import { ExploreSessionList } from './components/ExploreSessionList'
import { CreateProjectDialog } from './components/CreateProjectDialog'
import { SettingsPage } from './components/SettingsPage'
import { LogsPage } from './components/LogsPage'
import { ProjectGuard } from './components/ProjectGuard'

function KanbanView() {
  const { projectId } = useProject()
  const { data: projects, isLoading: projectsLoading } = useProjects()
  const { data: issues, isLoading } = useIssues(projectId ? { projectId } : undefined)
  const { data: agentStatus } = useAgentStatus()
  const [showCreateProject, setShowCreateProject] = useState(false)

  if (projectsLoading) {
    return null
  }

  if (projects && projects.length === 0) {
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
        <CreateProjectDialog open={showCreateProject} onClose={() => setShowCreateProject(false)} />
      </>
    )
  }

  return (
    <>
      {isLoading ? (
        <div className="flex items-center justify-center flex-1">
          <div className="text-gray-400">Loading...</div>
        </div>
      ) : (
        <KanbanBoard
          issues={issues ?? []}
          agentStatus={agentStatus ?? { running: false, issueId: null, issueNumber: null }}
        />
      )}
    </>
  )
}

function AppContent() {
  const { projectId, setProjectId, setProjects } = useProject()
  useSSE(projectId)

  const { data: projects } = useProjects()
  const { data: currentProject } = useCurrentProject()

  useEffect(() => {
    if (projects) {
      setProjects(projects)
    }
  }, [projects, setProjects])

  useEffect(() => {
    if (currentProject) {
      setProjectId(currentProject.id)
    } else if (projects && projects.length > 0 && !projectId) {
      setProjectId(projects[0].id)
    }
  }, [currentProject, projects, projectId, setProjectId])

  return (
    <div className="min-h-screen bg-gray-50 flex flex-col">
      <Header />
      <Routes>
        <Route element={<ProjectGuard />}>
          <Route path="/" element={<KanbanView />} />
          <Route path="/issue/:number" element={<IssueDetailPage />} />
          <Route path="/explore" element={<ExploreSessionList />} />
          <Route path="/explore/:id" element={<ExplorePage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/logs" element={<LogsPage />} />
        </Route>
      </Routes>
    </div>
  )
}

export default function App() {
  return (
    <ProjectProvider>
      <BrowserRouter>
        <AppContent />
      </BrowserRouter>
    </ProjectProvider>
  )
}
