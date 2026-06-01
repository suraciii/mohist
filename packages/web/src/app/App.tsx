import { useEffect, useState } from 'react'
import { BrowserRouter, Routes, Route, useLocation, Navigate } from 'react-router-dom'
import { useProjects } from '../entities/project'
import { LiveTaskProvider } from './providers/LiveTaskProvider'
import { ProjectProvider, useProject } from '../entities/project'
import { Header } from '../widgets/app-shell'
import { AppSidebar } from '../widgets/app-shell/ui/AppSidebar'
import { SidebarProvider, SidebarInset } from '@/shared/ui/components/sidebar'
import { IssueDetailPage } from '../pages/issue-detail/ui/IssueDetailPage'
import { IssueChangedFilesPage } from '../pages/issue-changed-files/ui/IssueChangedFilesPage'
import { SessionPage } from '../pages/session/ui/SessionPage'
import { CreateIssueDialog } from '../features/create-issue'
import { SettingsPage } from '../pages/settings/ui/SettingsPage'
import { ActivityPage } from '../pages/activity/ui/ActivityPage'
import { LogsPage } from '../pages/logs/ui/LogsPage'
import { ArchivedPage } from '../pages/archived/ui/ArchivedPage'
import { ProjectGuard, MobileBottomNav, FAB } from '../widgets/app-shell'
import { Toaster } from 'sonner'
import { HomePage } from '../pages/home/ui/HomePage'
import { EpicListPage } from '../pages/epics/ui/EpicListPage'
import { EpicDetailPage } from '../pages/epic-detail/ui/EpicDetailPage'

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
    if (projects && projects.length > 0 && (!projectId || !projects.some((project) => project.id === projectId))) {
      setProjectId(projects[0].id)
    }
  }, [projects, projectId, setProjectId])

  return (
    <SidebarProvider className="h-svh">
      <AppSidebar onCreateIssue={() => setCreateIssueOpen(true)} />
      <SidebarInset>
        <Header onCreateIssue={() => setCreateIssueOpen(true)} />
        <div className="flex-1 min-h-0 flex flex-col pb-14 md:pb-0">
          <Routes>
            <Route element={<ProjectGuard />}>
              <Route path="/" element={<HomePage />} />
              <Route path="/issue/:number" element={<NavigateToIssues />} />
              <Route path="/issue/:number/files" element={<NavigateToIssues />} />
              <Route path="/issue/:number/session/:sessionId" element={<NavigateToIssues />} />
              <Route path="/issue/:number/workflow/sessions/:sessionName" element={<NavigateToIssues />} />
              <Route path="/issues/:number" element={<IssueDetailPage />} />
              <Route path="/issues/:number/files" element={<IssueChangedFilesPage />} />
              <Route path="/issues/:number/session/:sessionId" element={<SessionPage />} />
              <Route path="/issues/:number/workflow/sessions/:sessionName" element={<SessionPage />} />
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
        </div>
        <MobileBottomNav />
      </SidebarInset>
      <CreateIssueDialog open={createIssueOpen} onClose={() => setCreateIssueOpen(false)} />
      <Toaster />
    </SidebarProvider>
  )
}

function NavigateToIssues() {
  const location = useLocation()
  return <Navigate to={location.pathname.replace(/^\/issue\//, '/issues/')} replace />
}

export default function App() {
  return (
    <ProjectProvider>
      <LiveTaskProvider>
        <BrowserRouter>
          <AppContent />
        </BrowserRouter>
      </LiveTaskProvider>
    </ProjectProvider>
  )
}
