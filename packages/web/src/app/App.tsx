import { lazy, Suspense, useEffect, useState } from 'react'
import { BrowserRouter, Routes, Route, useLocation, Navigate, useParams, Outlet } from 'react-router-dom'
import { useProjects } from '../entities/project'
import { LiveTaskProvider } from './providers/LiveTaskProvider'
import { ThemeProvider } from '../shared/lib/theme/ThemeProvider'
import { ProjectProvider, useProject, projectPath } from '../entities/project'
import { AppSidebar, Header } from '../widgets/app-shell'
import { SidebarProvider, SidebarInset } from '@/shared/ui/components/sidebar'
import { isApplicationSection, isProjectSection, isSettingsSectionKey } from '../shared/config/settings-sections'
import { ProjectGuard, MobileBottomNav, FAB } from '../widgets/app-shell'
import { Toaster } from 'sonner'
import { RuntimeToastHost } from '../shared/ui/toast'

const IssueDetailPage = lazy(() => import('../pages/issue-detail').then(({ IssueDetailPage }) => ({ default: IssueDetailPage })))
const IssueChangedFilesPage = lazy(() => import('../pages/issue-changed-files').then(({ IssueChangedFilesPage }) => ({ default: IssueChangedFilesPage })))
const UnifiedSessionPage = lazy(() => import('../pages/session').then(({ UnifiedSessionPage }) => ({ default: UnifiedSessionPage })))
const CreateIssueDialog = lazy(() => import('../features/create-issue').then(({ CreateIssueDialog }) => ({ default: CreateIssueDialog })))
const SettingsPage = lazy(() => import('../pages/settings').then(({ SettingsPage }) => ({ default: SettingsPage })))
const ActivityPage = lazy(() => import('../pages/activity').then(({ ActivityPage }) => ({ default: ActivityPage })))
const RunnersPage = lazy(() => import('../pages/runners').then(({ RunnersPage }) => ({ default: RunnersPage })))
const LogsPage = lazy(() => import('../pages/logs').then(({ LogsPage }) => ({ default: LogsPage })))
const ArchivedPage = lazy(() => import('../pages/archived').then(({ ArchivedPage }) => ({ default: ArchivedPage })))
const DashboardPage = lazy(() => import('../pages/dashboard').then(({ DashboardPage }) => ({ default: DashboardPage })))
const IssuesPage = lazy(() => import('../pages/issues').then(({ IssuesPage }) => ({ default: IssuesPage })))
const EpicListPage = lazy(() => import('../pages/epics').then(({ EpicListPage }) => ({ default: EpicListPage })))
const EpicDetailPage = lazy(() => import('../pages/epic-detail').then(({ EpicDetailPage }) => ({ default: EpicDetailPage })))
const RunnerDetailPage = lazy(() => import('../pages/runner-detail').then(({ RunnerDetailPage }) => ({ default: RunnerDetailPage })))
const InboxPage = lazy(() => import('../pages/inbox').then(({ InboxPage }) => ({ default: InboxPage })))
const InsightsPage = lazy(() => import('../pages/insights').then(({ InsightsPage }) => ({ default: InsightsPage })))
const AgentListPage = lazy(() => import('../pages/agent-list').then(({ AgentListPage }) => ({ default: AgentListPage })))
const AgentDetailPage = lazy(() => import('../pages/agent-detail').then(({ AgentDetailPage }) => ({ default: AgentDetailPage })))
const AgentSessionComposerPage = lazy(() => import('../pages/agent-session-composer').then(({ AgentSessionComposerPage }) => ({ default: AgentSessionComposerPage })))
const ConnectionDiagnosticPage = lazy(() => import('../pages/connection-diagnostic').then(({ ConnectionDiagnosticPage }) => ({ default: ConnectionDiagnosticPage })))

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
          <Suspense fallback={<div className="flex min-h-0 flex-1 items-center justify-center text-sm text-muted-foreground">Loading…</div>}>
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
                  <Route path="sessions/:sessionId" element={<UnifiedSessionPage />} />
                  <Route path="agents" element={<AgentListPage />} />
                  <Route path="agents/:agentId" element={<AgentDetailPage />} />
                  <Route path="connections/:connectionId" element={<ConnectionDiagnosticPage />} />
                  <Route path="agent-sessions/new" element={<AgentSessionComposerPage />} />
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
          </Suspense>
          {shouldShowCreateIssueFab(location.pathname) && <FAB onClick={() => setCreateIssueOpen(true)} />}
        </div>
        <MobileBottomNav />
      </SidebarInset>
      {createIssueOpen && (
        <Suspense fallback={null}>
          <CreateIssueDialog open onClose={() => setCreateIssueOpen(false)} />
        </Suspense>
      )}
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
