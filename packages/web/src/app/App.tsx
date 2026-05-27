import { useEffect, useState } from 'react'
import { BrowserRouter, Routes, Route, useLocation, Navigate } from 'react-router-dom'
import { useIssues, useArchivedIssues, useProjects, useAgentStatus } from '../entities/project/api/queries'
import { LiveTaskProvider } from '../entities/project/model/LiveTaskProvider'
import { ProjectProvider, useProject } from '../entities/project/model/ProjectContext'
import { KanbanBoard } from '../widgets/kanban-board/ui/KanbanBoard'
import { Header } from '../widgets/app-shell/ui/Header'
import { IssueDetailPage } from '../pages/issue-detail/ui/IssueDetailPage'
import { IssueChangedFilesPage } from '../pages/issue-changed-files/ui/IssueChangedFilesPage'
import { SessionPage } from '../pages/session/ui/SessionPage'
import { CreateProjectDialog } from '../features/create-project/ui/CreateProjectDialog'
import { CreateIssueDialog } from '../features/create-issue/ui/CreateIssueDialog'
import { SettingsPage } from '../pages/settings/ui/SettingsPage'
import { ActivityPage } from '../pages/activity/ui/ActivityPage'
import { LogsPage } from '../pages/logs/ui/LogsPage'
import { ArchivedPage } from '../pages/archived/ui/ArchivedPage'
import { ProjectGuard } from '../widgets/app-shell/ui/ProjectGuard'
import { MobileBottomNav } from '../widgets/app-shell/ui/MobileBottomNav'
import { FAB } from '../widgets/app-shell/ui/FAB'
import { Toaster } from 'sonner'
import { useDocumentTitle } from '../shared/lib/useDocumentTitle'
import { EpicListPage } from '../pages/epics/ui/EpicListPage'
import { EpicDetailPage } from '../pages/epic-detail/ui/EpicDetailPage'

function KanbanView() {
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
          agentStatus={agentStatus ?? { running: false, issueId: null, issueNumber: null, activeAgents: [], capacity: { active: 0, max: 8 } }}
          archivedCount={archivedIssues?.length ?? 0}
        />
      )}
    </>
  )
}

function AppContent() {
  const { projectId, setProjectId, setProjects } = useProject()
  const location = useLocation()

  const { data: projects } = useProjects()
  const [createIssueOpen, setCreateIssueOpen] = useState(false)

  useEffect(() => {
    if (projects) {
      setProjects(projects)
    }
  }, [projects, setProjects])

  useEffect(() => {
    if (projects && projects.length > 0 && !projectId) {
      setProjectId(projects[0].id)
    }
  }, [projects, projectId, setProjectId])

  return (
    <LiveTaskProvider>
    <div className="min-h-screen bg-gray-50 flex flex-col pb-14 md:pb-0">
      <Toaster />
      <Header onCreateIssue={() => setCreateIssueOpen(true)} />
      <MobileBottomNav />
      <Routes>
        <Route element={<ProjectGuard />}>
          <Route path="/" element={<KanbanView />} />
          <Route path="/issue/:number" element={<IssueDetailPage />} />
          <Route path="/issue/:number/files" element={<IssueChangedFilesPage />} />
          <Route path="/issue/:number/session/:sessionId" element={<SessionPage />} />
          <Route path="/activity" element={<ActivityPage />} />
          <Route path="/settings" element={<Navigate to="/settings/ai" replace />} />
          <Route path="/settings/:section" element={<SettingsPage />} />
          <Route path="/logs" element={<LogsPage />} />
          <Route path="/archived" element={<ArchivedPage />} />
          <Route path="/epics" element={<EpicListPage />} />
          <Route path="/epic/:id" element={<EpicDetailPage />} />
        </Route>
      </Routes>
      {location.pathname === '/' && <FAB onClick={() => setCreateIssueOpen(true)} />}
      <CreateIssueDialog open={createIssueOpen} onClose={() => setCreateIssueOpen(false)} />
    </div>
    </LiveTaskProvider>
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
