import { useLocation, useParams } from 'react-router-dom'
import { SidebarTrigger, useSidebar } from '@/shared/ui/components/sidebar'
import { Button } from '@/shared/ui/components/button'
import { PlusIcon } from 'lucide-react'
import { useAgentStatus, useAgent } from '../../../entities/agent'
import { useEpic } from '../../../entities/epic'

function findEpicNumberSegment(pathname: string): number | null {
  const segments = pathname.split('/').filter(Boolean)
  for (let i = 0; i < segments.length - 1; i += 1) {
    if (segments[i] !== 'epics') continue
    const number = Number(segments[i + 1])
    return Number.isInteger(number) && number > 0 ? number : null
  }
  return null
}

function formatEpicTitle(
  epicLoading: boolean,
  epic: { number: number } | undefined,
  fallbackNumber: number | null,
): string {
  if (epicLoading) return 'Epic #\u2026'
  if (typeof epic?.number === 'number') return `Epic #${epic.number}`
  if (fallbackNumber !== null) return `Epic #${fallbackNumber}`
  return 'Epic'
}

export interface HeaderDataHooks {
  epicHook: typeof useEpic
  agentHook: typeof useAgent
  agentStatusHook: typeof useAgentStatus
}

const defaultDataHooks: HeaderDataHooks = {
  epicHook: useEpic,
  agentHook: useAgent,
  agentStatusHook: useAgentStatus,
}

function usePageTitle(dataHooks: HeaderDataHooks): string {
  const location = useLocation()
  const params = useParams<{ number?: string; section?: string; agentId?: string }>()
  const segments = location.pathname.split('/').filter(Boolean)
  const firstSegment = segments[0] ?? ''
  const section = segments.length > 1 ? `/${segments.slice(1).join('/')}` : '/'
  const epicNumberSegment = findEpicNumberSegment(location.pathname)
  const { data: epic, isLoading: epicLoading } = dataHooks.epicHook(epicNumberSegment)
  const agentId = params.agentId ?? (segments[0] === 'agents' ? segments[1] : undefined)
  const { data: agent } = dataHooks.agentHook(agentId ?? '')

  if (firstSegment === 'issues') {
    return segments.length > 1 ? `Issue #${params.number ?? segments[1]}` : 'Issues'
  }
  if (firstSegment === 'activity') return 'Activity'
  if (firstSegment === 'epics') {
    if (segments.length > 1) return formatEpicTitle(epicLoading, epic, epicNumberSegment)
    return 'Epics'
  }
  if (firstSegment === 'workspaces') {
    if (segments.length > 1) return `Workspace ${decodeURIComponent(segments[1])}`
    return 'Workspaces'
  }
  if (firstSegment === 'agents') {
    if (segments.length > 1 && agent) return agent.name
    if (segments.length > 1) return `Agent #${agentId?.slice(0, 8)}`
    return 'Agents'
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
    return formatEpicTitle(epicLoading, epic, epicNumberSegment)
  }
  if (section === '/workspaces') return 'Workspaces'
  if (section.startsWith('/workspaces/')) {
    return `Workspace ${decodeURIComponent(section.split('/')[2])}`
  }
  if (section.startsWith('/issues/')) {
    return `Issue #${params.number ?? section.split('/')[2]}`
  }
  if (section.startsWith('/agents')) {
    if (section !== '/agents' && agent) return agent.name
    return 'Agents'
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

export function Header({
  onCreateIssue,
  dataHooks,
}: {
  onCreateIssue: () => void
  dataHooks?: Partial<HeaderDataHooks>
}) {
  const resolvedDataHooks = { ...defaultDataHooks, ...dataHooks }
  const { isMobile } = useSidebar()
  const title = usePageTitle(resolvedDataHooks)
  const isSettingsRoute = useIsSettingsRoute()
  const { data: agentStatus } = resolvedDataHooks.agentStatusHook()
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
