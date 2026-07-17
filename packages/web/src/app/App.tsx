import { useEffect, useState } from 'react'
import { BrowserRouter, Routes, Route, useLocation, Navigate, useParams, Outlet } from 'react-router-dom'
import { useProjects } from '../entities/project'
import { LiveTaskProvider } from './providers/LiveTaskProvider'
import { ThemeProvider } from '../shared/lib/theme/ThemeProvider'
import { ProjectProvider, useProject, projectPath } from '../entities/project'
import { AppSidebar, Header } from '../widgets/app-shell'
import { SidebarProvider, SidebarInset } from '@/shared/ui/components/sidebar'
import { IssueDetailPage } from '../pages/issue-detail'
import { IssueChangedFilesPage } from '../pages/issue-changed-files'
import { GenericSessionPage, SessionPage } from '../pages/session'
import { CreateIssueDialog } from '../features/create-issue'
import { isApplicationSection, isProjectSection, isSettingsSectionKey, SettingsPage } from '../pages/settings'
import { ActivityPage } from '../pages/activity'
import { RunnersPage } from '../pages/runners'
import { LogsPage } from '../pages/logs'
import { ArchivedPage } from '../pages/archived'
import { ProjectGuard, MobileBottomNav, FAB } from '../widgets/app-shell'
import { Toaster } from 'sonner'
import { RuntimeToastHost } from '../shared/ui/toast'
import { DashboardPage } from '../pages/dashboard'
import { IssuesPage } from '../pages/issues'
import { EpicListPage } from '../pages/epics'
import { EpicDetailPage } from '../pages/epic-detail'
import { RunnerDetailPage } from '../pages/runner-detail'
import { InboxPage } from '../pages/inbox'
import { InsightsPage } from '../pages/insights'
import { AgentListPage } from '../pages/agent-list'
import { AgentDetailPage } from '../pages/agent-detail'
import { AgentSessionComposerPage } from '../pages/agent-session-composer'

export function AppContent() {
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
        <div className="flex-1 min-h-0 min-w-0 flex flex-col pb-[calc(3.5rem+env(safe-area-inset-bottom))] md:pb-0">
          <Routes>
            <Route path="/settings" element={<Navigate to="/settings/ai" replace />} />
            <Route path="/settings/:section" element={<ApplicationSettingsSection />} />
            <Route element={<ProjectGuard />}>
              <Route path="/" element={<NavigateToCurrentProject />} />
              <Route path="/:projectName" element={<ProjectRouteScope />}>
                <Route index element={<DashboardPage />} />
                <Route path="issues" element={<IssuesPage />} />
                <Route path="issues/:number" element={<IssueDetailPage />} />
                <Route path="issues/:number/files" element={<IssueChangedFilesPage />} />
                <Route path="issues/:number/session/:sessionId" element={<SessionPage />} />
                <Route path="issues/:number/workflow/sessions/:sessionName" element={<SessionPage />} />
                <Route path="agents" element={<AgentListPage />} />
                <Route path="agents/:agentId" element={<AgentDetailPage />} />
                <Route path="agent-sessions/new" element={<AgentSessionComposerPage />} />
                <Route path="agent-sessions/:sessionId" element={<GenericSessionPage />} />
                <Route path="activity" element={<ActivityPage />} />
                <Route path="runners" element={<RunnersPage />} />
                <Route path="settings" element={<LegacySettingsRedirect />} />
                <Route path="settings/:section" element={<LegacyProjectSettingsSection />} />
                <Route path="logs" element={<LogsPage />} />
                <Route path="archived" element={<ArchivedPage />} />
                <Route path="epics" element={<EpicListPage />} />
                <Route path="epics/:number" element={<EpicDetailPage />} />
                <Route path="inbox" element={<InboxPage />} />
                <Route path="insights" element={<InsightsPage />} />
                <Route path="runners/:runnerId" element={<RunnerDetailPage />} />
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

function ApplicationSettingsSection() {
  const { section } = useParams<{ section: string }>()
  const { currentProject } = useProject()

  if (section && isSettingsSectionKey(section) && isProjectSection(section)) {
    if (currentProject) {
      return <Navigate to={`/${encodeURIComponent(currentProject.name)}/settings/${section}`} replace />
    }
  }
  return <SettingsPage />
}

function LegacySettingsRedirect() {
  return <Navigate to="/settings/ai" replace />
}

function LegacyProjectSettingsSection() {
  const { section } = useParams<{ section: string }>()
  if (section && isSettingsSectionKey(section) && isApplicationSection(section)) {
    return <Navigate to={`/settings/${section}`} replace />
  }
  return <SettingsPage />
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
    <ThemeProvider>
      <ProjectProvider>
        <RuntimeToastHost>
          <LiveTaskProvider>
            <BrowserRouter>
              <AppContent />
            </BrowserRouter>
          </LiveTaskProvider>
        </RuntimeToastHost>
      </ProjectProvider>
    </ThemeProvider>
  )
}
