import { useState, useRef, useEffect } from 'react'
import { useNavigate, useLocation } from 'react-router-dom'
import {
  LayoutDashboardIcon,
  ListTodoIcon,
  ActivityIcon,
  ServerIcon,
  ArchiveIcon,
  FileTextIcon,
  SettingsIcon,
  PlusIcon,
  ChevronDownIcon,
  FolderIcon,
  FolderGit2Icon,
  PowerIcon,
  PowerOffIcon,
  InboxIcon,
  BotIcon,
  SparklesIcon,
} from 'lucide-react'
import { useProject, useProjectPath } from '../../../entities/project'
import { useAgentStatus } from '../../../entities/agent'
import { useDeleteProject } from '../../../entities/project'
import { useUnreadInboxCount } from '../../../entities/inbox'
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuBadge,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarRail,
  SidebarSeparator,
} from '@/shared/ui/components/sidebar'
import { Button } from '@/shared/ui/components/button'
import { CreateProjectDialog } from '../../../features/create-project'
import type { Project } from '../../../entities/project'
import type { ComponentType } from 'react'

interface AppSidebarProps {
  onCreateIssue: () => void
}

type NavScope = 'application' | 'project'

interface NavItem {
  key: string
  label: string
  icon: ComponentType<{ className?: string }>
  to: string
  scope: NavScope
}

const primaryNav: readonly NavItem[] = [
  { key: 'dashboard', label: 'Dashboard', icon: LayoutDashboardIcon, to: '/', scope: 'project' },
  { key: 'insights', label: 'Insights', icon: SparklesIcon, to: '/insights', scope: 'project' },
  { key: 'issues', label: 'Issues', icon: ListTodoIcon, to: '/issues', scope: 'project' },
  { key: 'agents', label: 'Agents', icon: BotIcon, to: '/agents', scope: 'project' },
  { key: 'inbox', label: 'Inbox', icon: InboxIcon, to: '/inbox', scope: 'project' },
  { key: 'activity', label: 'Activity', icon: ActivityIcon, to: '/activity', scope: 'project' },
  { key: 'runners', label: 'Runners', icon: ServerIcon, to: '/runners', scope: 'project' },
  { key: 'epics', label: 'Epics', icon: ListTodoIcon, to: '/epics', scope: 'project' },
  { key: 'workspaces', label: 'Workspaces', icon: FolderGit2Icon, to: '/workspaces', scope: 'project' },
]

const archivedNav: readonly NavItem[] = [
  { key: 'archived', label: 'Archived', icon: ArchiveIcon, to: '/archived', scope: 'project' },
]

const configureNav: readonly NavItem[] = [
  { key: 'logs', label: 'Logs', icon: FileTextIcon, to: '/logs', scope: 'project' },
  { key: 'settings', label: 'Settings', icon: SettingsIcon, to: '/settings/ai', scope: 'application' },
]

function isNavActive(pathname: string, to: string): boolean {
  if (to === '/') return pathname === '/'
  return pathname === to || pathname.startsWith(`${to}/`)
}

function isSettingsPathActive(pathname: string): boolean {
  return pathname.split('/').filter(Boolean).includes('settings')
}

function ProjectSwitcher({ onNavigate }: { onNavigate?: () => void }) {
  const { projectId, setProjectId, projects, currentProject } = useProject()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const [createOpen, setCreateOpen] = useState(false)
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  const deleteProject = useDeleteProject()

  useEffect(() => {
    function handle(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false)
      }
    }
    if (open) {
      document.addEventListener('mousedown', handle)
      return () => document.removeEventListener('mousedown', handle)
    }
  }, [open])

  useEffect(() => {
    function reveal() {
      setOpen(true)
    }
    window.addEventListener('mohist:sidebar:open-project-switcher', reveal)
    return () => window.removeEventListener('mohist:sidebar:open-project-switcher', reveal)
  }, [])

  function handleSelect(project: Project) {
    setProjectId(project.id)
    setOpen(false)
    navigate(`/${encodeURIComponent(project.name)}`)
    onNavigate?.()
  }

  function handleDelete() {
    if (!currentProject) return
    deleteProject.mutate(currentProject.id, {
      onSuccess: () => {
        setDeleteConfirmOpen(false)
        setOpen(false)
        const remaining = projects.filter((p) => p.id !== currentProject.id)
        const nextProject = remaining[0] ?? null
        setProjectId(nextProject?.id ?? null)
        navigate(nextProject ? `/${encodeURIComponent(nextProject.name)}` : '/')
        onNavigate?.()
      },
    })
  }

  if (projects.length === 0) {
    return (
      <div className="px-2 py-1.5 text-xs text-sidebar-foreground/60">
        No projects
      </div>
    )
  }

  return (
    <>
      <div className="relative" ref={ref}>
        <Button
          variant="ghost"
          onClick={() => setOpen(!open)}
          data-testid="project-switcher"
          className="w-full justify-between gap-1.5 px-2.5 py-1.5 h-auto font-medium text-sm text-sidebar-foreground hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
        >
          <span className="flex items-center gap-2 min-w-0">
            <FolderIcon className="size-4 shrink-0" />
            <span className="truncate">
              {currentProject?.name ?? 'Select project'}
            </span>
          </span>
          <ChevronDownIcon
            className={`size-3.5 text-sidebar-foreground/60 transition-transform ${
              open ? 'rotate-180' : ''
            }`}
          />
        </Button>

        {open && (
          <div className="absolute top-full left-0 right-0 mt-1 rounded-md border border-sidebar-border bg-popover shadow-lg py-1 z-50">
            {projects.map((project) => (
              <button
                key={project.id}
                type="button"
                onClick={() => handleSelect(project)}
                className={`w-full text-left px-3 py-2 text-sm hover:bg-muted ${
                  project.id === projectId
                    ? 'text-blue-600 bg-blue-50 font-medium'
                    : 'text-popover-foreground'
                }`}
              >
                <div className="font-medium truncate">{project.name}</div>
              </button>
            ))}
            <div className="border-t my-1" />
            <button
              type="button"
              onClick={() => {
                setOpen(false)
                setCreateOpen(true)
              }}
              className="w-full text-left px-3 py-2 text-sm text-popover-foreground hover:bg-muted inline-flex items-center gap-2"
            >
              <PlusIcon className="size-3.5" />
              New Project
            </button>
            {currentProject && (
              <button
                type="button"
                onClick={() => {
                  setOpen(false)
                  setDeleteConfirmOpen(true)
                }}
                className="w-full text-left px-3 py-2 text-sm text-red-600 hover:bg-red-50 inline-flex items-center gap-2"
              >
                Delete Project
              </button>
            )}
          </div>
        )}
      </div>

      <CreateProjectDialog
        open={createOpen}
        onClose={() => setCreateOpen(false)}
      />

      {deleteConfirmOpen && currentProject && (
        <div className="fixed inset-0 z-50 grid place-items-center bg-black/40 supports-backdrop-filter:backdrop-blur-xs">
          <div className="bg-popover rounded-xl border shadow-lg p-4 w-full max-w-sm mx-4">
            <h3 className="font-heading text-base font-medium mb-2">
              Delete Project
            </h3>
            <p className="text-sm text-muted-foreground mb-4">
              Are you sure you want to delete{' '}
              <span className="font-medium text-foreground">
                {currentProject.name}
              </span>
              ? This will also delete all associated issues.
            </p>
            {deleteProject.isError && (
              <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600 mb-3">
                {(deleteProject.error as Error).message}
              </div>
            )}
            <div className="flex justify-end gap-2">
              <Button
                variant="outline"
                onClick={() => setDeleteConfirmOpen(false)}
              >
                Cancel
              </Button>
              <Button
                variant="destructive"
                onClick={handleDelete}
                disabled={deleteProject.isPending}
              >
                {deleteProject.isPending ? 'Deleting...' : 'Delete'}
              </Button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}

function AgentStatusFooter() {
  const { data: agentStatus } = useAgentStatus()
  const capacity = agentStatus?.capacity
  const running = agentStatus?.running ?? false
  const active = capacity?.active ?? 0
  const max = capacity?.max ?? 8
  const pct = max > 0 ? Math.min(100, Math.round((active / max) * 100)) : 0

  return (
    <div className="rounded-md px-2 py-2 text-xs space-y-1.5">
      <div className="flex items-center gap-2">
        {running ? (
          <PowerIcon className="size-3.5 text-green-600" />
        ) : (
          <PowerOffIcon className="size-3.5 text-muted-foreground" />
        )}
        <span className="font-medium text-sidebar-foreground">
          {running ? 'Runner active' : 'Runner idle'}
        </span>
      </div>
      <div className="flex items-center justify-between text-sidebar-foreground/70">
        <span>Capacity</span>
        <span className="font-mono">
          {active} / {max}
        </span>
      </div>
      <div className="h-1 rounded-full bg-sidebar-accent overflow-hidden">
        <div
          className="h-full bg-blue-500 transition-all"
          style={{ width: `${pct}%` }}
        />
      </div>
    </div>
  )
}

export function AppSidebar({ onCreateIssue }: AppSidebarProps) {
  const location = useLocation()
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { data: unreadCount } = useUnreadInboxCount()

  function renderNavItem(item: NavItem) {
    const to = item.scope === 'application' ? item.to : toProjectPath(item.to)
    const active = item.key === 'settings'
      ? isSettingsPathActive(location.pathname)
      : isNavActive(location.pathname, to)
    const Icon = item.icon
    return (
      <SidebarMenuItem key={item.key}>
        <SidebarMenuButton
          isActive={active}
          onClick={() => navigate(to)}
          data-testid={`nav-${item.key}`}
        >
          <Icon />
          <span>{item.label}</span>
        </SidebarMenuButton>
        {item.key === 'inbox' && unreadCount != null && unreadCount > 0 && (
          <SidebarMenuBadge data-testid="nav-inbox-badge">{unreadCount}</SidebarMenuBadge>
        )}
      </SidebarMenuItem>
    )
  }

  return (
    <Sidebar collapsible="icon" variant="sidebar">
      <SidebarHeader>
        <div className="flex items-center justify-between px-1 group-data-[collapsible=icon]:justify-center">
          <span className="text-base font-bold tracking-tight group-data-[collapsible=icon]:hidden">
            mohist
          </span>
          <span className="hidden group-data-[collapsible=icon]:block text-base font-bold tracking-tight">
            m
          </span>
        </div>
        <div className="group-data-[collapsible=icon]:hidden">
          <ProjectSwitcher onNavigate={() => undefined} />
        </div>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton
              isActive={false}
              onClick={onCreateIssue}
              data-testid="sidebar-new-issue"
              className="bg-primary text-primary-foreground hover:bg-primary/90 hover:text-primary-foreground data-active:bg-primary data-active:text-primary-foreground"
            >
              <PlusIcon />
              <span>New Issue</span>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
        <div className="hidden group-data-[collapsible=icon]:block">
          <Button
            variant="default"
            size="icon-sm"
            onClick={onCreateIssue}
            aria-label="New Issue"
            data-testid="sidebar-new-issue-icon"
          >
            <PlusIcon />
          </Button>
        </div>
      </SidebarHeader>

      <SidebarSeparator />

      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupLabel>Workspace</SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              {primaryNav.map((item) => renderNavItem(item))}
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>

        <SidebarGroup>
          <SidebarGroupLabel>Configure</SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              {configureNav.map((item) => renderNavItem(item))}
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>

        <SidebarGroup>
          <SidebarGroupContent>
            <SidebarMenu>
              {archivedNav.map((item) => renderNavItem(item))}
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>
      </SidebarContent>

      <SidebarSeparator />

      <SidebarFooter>
        <div className="group-data-[collapsible=icon]:hidden">
          <AgentStatusFooter />
        </div>
      </SidebarFooter>

      <SidebarRail />
    </Sidebar>
  )
}
