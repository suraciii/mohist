import { useLocation, useParams } from 'react-router-dom'
import { SidebarTrigger, useSidebar } from '@/shared/ui/components/sidebar'
import { Button } from '@/shared/ui/components/button'
import { PlusIcon } from 'lucide-react'
import { useAgentStatus } from '../../../entities/agent'
import { useEpic } from '../../../entities/epic'

function findEpicIdSegment(pathname: string): string | null {
  const segments = pathname.split('/').filter(Boolean)
  for (let i = 0; i < segments.length - 1; i += 1) {
    if (segments[i] === 'epics') return segments[i + 1] || null
  }
  return null
}

function formatEpicTitle(
  epicLoading: boolean,
  epic: { number: number | null } | undefined,
  fallbackSegment: string | null,
): string {
  if (epicLoading) return 'Epic #\u2026'
  if (typeof epic?.number === 'number') return `Epic #${epic.number}`
  if (fallbackSegment) return `Epic #${fallbackSegment.slice(0, 8)}`
  return 'Epic'
}

function usePageTitle(): string {
  const location = useLocation()
  const params = useParams<{ number?: string; id?: string; section?: string }>()
  const segments = location.pathname.split('/').filter(Boolean)
  const firstSegment = segments[0] ?? ''
  const section = segments.length > 1 ? `/${segments.slice(1).join('/')}` : '/'
  const epicIdSegment = findEpicIdSegment(location.pathname)
  const { data: epic, isLoading: epicLoading } = useEpic(epicIdSegment ?? '')

  if (firstSegment === 'issues') {
    return segments.length > 1 ? `Issue #${params.number ?? segments[1]}` : 'Issues'
  }
  if (firstSegment === 'activity') return 'Activity'
  if (firstSegment === 'epics') {
    if (segments.length > 1) return formatEpicTitle(epicLoading, epic, epicIdSegment)
    return 'Epics'
  }
  if (firstSegment === 'archived') return 'Archived'
  if (firstSegment === 'logs') return 'Logs'
  if (firstSegment === 'settings') {
    const sub = params.section
    if (!sub || sub === 'ai') return 'Settings'
    return `Settings · ${sub.charAt(0).toUpperCase()}${sub.slice(1)}`
  }

  if (section === '/issues') return 'Issues'
  if (section.startsWith('/activity')) return 'Activity'
  if (section === '/epics') return 'Epics'
  if (section.startsWith('/epics/')) {
    return formatEpicTitle(epicLoading, epic, epicIdSegment)
  }
  if (section.startsWith('/issues/')) {
    return `Issue #${params.number ?? section.split('/')[2]}`
  }
  if (section === '/archived') return 'Archived'
  if (section.startsWith('/logs')) return 'Logs'
  if (section.startsWith('/settings')) {
    const section = params.section
    if (!section || section === 'ai') return 'Settings'
    return `Settings · ${section.charAt(0).toUpperCase()}${section.slice(1)}`
  }

  if (section === '/') return 'Dashboard'
  return 'Mohist'
}

function useIsSettingsRoute(): boolean {
  const location = useLocation()
  const segments = location.pathname.split('/').filter(Boolean)
  return segments.includes('settings')
}

export function Header({ onCreateIssue }: { onCreateIssue: () => void }) {
  const { isMobile } = useSidebar()
  const title = usePageTitle()
  const isSettingsRoute = useIsSettingsRoute()
  const { data: agentStatus } = useAgentStatus()
  const running = agentStatus?.running ?? false

  return (
    <header className="h-12 shrink-0 flex items-center gap-2 border-b bg-background px-3 md:px-4">
      <SidebarTrigger className="-ml-1" />
      <div className="flex items-center gap-2 min-w-0">
        {isMobile && (
          <span className="text-sm font-bold tracking-tight">mohist</span>
        )}
        {!isSettingsRoute && (
          <h1 className="text-sm font-medium text-foreground truncate">{title}</h1>
        )}
      </div>
      <div className="ml-auto flex items-center gap-2">
        {running && (
          <span
            data-testid="header-runner-status"
            className="hidden sm:inline-flex items-center gap-1.5 text-xs text-green-700 bg-green-50 px-2 py-0.5 rounded-full"
          >
            <span className="inline-block h-1.5 w-1.5 rounded-full bg-green-500 animate-pulse" />
            Runner active
          </span>
        )}
        {!isMobile && !isSettingsRoute && (
          <Button
            size="sm"
            onClick={onCreateIssue}
            data-testid="header-new-issue"
            className="h-8"
          >
            <PlusIcon />
            New Issue
          </Button>
        )}
      </div>
    </header>
  )
}
