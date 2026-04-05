import { useEffect } from 'react'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import { useIssues, useProjects, useAgentStatus } from './hooks/useQueries'
import useSSE from './hooks/useSSE'
import { ProjectProvider, useProject } from './context/ProjectContext'
import { KanbanBoard } from './components/KanbanBoard'
import { Header } from './components/Header'
import { IssueDetailPage } from './components/IssueDetailPage'

function KanbanView() {
  const { projectId, setProjectId, setProjects } = useProject()
  const { data: projects } = useProjects()
  const { data: issues, isLoading } = useIssues(projectId ? { projectId } : undefined)
  const { data: agentStatus } = useAgentStatus()

  useEffect(() => {
    if (projects && projects.length > 0) {
      setProjects(projects)
      if (!projectId) {
        setProjectId(projects[0].id)
      }
    }
  }, [projects, projectId, setProjectId, setProjects])

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
  const { projectId } = useProject()
  useSSE(projectId)

  return (
    <div className="min-h-screen bg-gray-50 flex flex-col">
      <Header />
      <Routes>
        <Route path="/" element={<KanbanView />} />
        <Route path="/issue/:number" element={<IssueDetailPage />} />
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
