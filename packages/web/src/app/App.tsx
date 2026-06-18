import { useEffect, useState } from 'react'
import { BrowserRouter, Routes, Route, useLocation, Navigate, useParams, Outlet } from 'react-router-dom'
import { useProjects } from '../entities/project'
import { LiveTaskProvider } from './providers/LiveTaskProvider'
import { ProjectProvider, useProject, projectPath } from '../entities/project'
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
import { RuntimeToastHost } from '../shared/ui/toast'
import { DashboardPage } from '../pages/dashboard/ui/DashboardPage'
import { IssuesPage } from '../pages/issues/ui/IssuesPage'
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
              <Route path="/" element={<NavigateToCurrentProject />} />
              <Route path="/:projectName" element={<ProjectRouteScope />}>
                <Route index element={<DashboardPage />} />
                <Route path="issues" element={<IssuesPage />} />
                <Route path="issues/:number" element={<IssueDetailPage />} />
                <Route path="issues/:number/files" element={<IssueChangedFilesPage />} />
                <Route path="issues/:number/session/:sessionId" element={<SessionPage />} />
                <Route path="issues/:number/workflow/sessions/:sessionName" element={<SessionPage />} />
                <Route path="activity" element={<ActivityPage />} />
                <Route path="settings" element={<Navigate to="settings/ai" replace />} />
                <Route path="settings/:section" element={<SettingsPage />} />
                <Route path="logs" element={<LogsPage />} />
                <Route path="archived" element={<ArchivedPage />} />
                <Route path="epics" element={<EpicListPage />} />
                <Route path="epics/:id" element={<EpicDetailPage />} />
              </Route>
            </Route>
          </Routes>
          {shouldShowCreateIssueFab(location.pathname) && <FAB onClick={() => setCreateIssueOpen(true)} />}
        </div>
        <MobileBottomNav />
      </SidebarInset>
      <CreateIssueDialog open={createIssueOpen} onClose={() => setCreateIssueOpen(false)} />
      <Toaster />
    </SidebarProvider>
  )
}

function NavigateToCurrentProject() {
  const { currentProject } = useProject()
  return <Navigate to={projectPath(currentProject?.name)} replace />
}

function ProjectRouteScope() {
  const { projectName } = useParams<{ projectName?: string }>()
  const { projects, setProjectId, currentProject } = useProject()

  useEffect(() => {
    if (!projectName || projects.length === 0) return
    const project = projects.find((candidate) => candidate.name === projectName)
    if (project && project.id !== currentProject?.id) {
      setProjectId(project.id)
    }
  }, [currentProject?.id, projectName, projects, setProjectId])

  return <ProjectNameGuard projectName={projectName} />
}

function ProjectNameGuard({ projectName }: { projectName?: string }) {
  const { projects, currentProject } = useProject()

  if (!projectName || projects.length === 0) return <DashboardPage />
  const project = projects.find((candidate) => candidate.name === projectName)
  if (!project) {
    return <Navigate to="/" replace />
  }
  if (currentProject?.id !== project.id) return null

  return <Outlet />
}

function shouldShowCreateIssueFab(pathname: string) {
  const segments = pathname.split('/').filter(Boolean)
  if (segments.length === 1) return true
  if (segments.length === 2 && segments[1] === 'issues') return true
  return false
}

export default function App() {
  return (
    <ProjectProvider>
      <RuntimeToastHost>
        <LiveTaskProvider>
          <BrowserRouter>
            <AppContent />
          </BrowserRouter>
        </LiveTaskProvider>
      </RuntimeToastHost>
    </ProjectProvider>
  )
}
