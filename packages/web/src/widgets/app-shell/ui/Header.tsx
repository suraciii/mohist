import { useLocation, useParams } from 'react-router-dom'
import { SidebarTrigger, useSidebar } from '@/shared/ui/components/sidebar'
import { Button } from '@/shared/ui/components/button'
import { PlusIcon } from 'lucide-react'
import { useAgentStatus } from '../../../entities/agent'

function usePageTitle(): string {
  const location = useLocation()
  const params = useParams<{ number?: string; id?: string; section?: string }>()

  if (location.pathname === '/') return 'Board'
  if (location.pathname.startsWith('/activity')) return 'Activity'
  if (location.pathname === '/epics') return 'Epics'
  if (location.pathname.startsWith('/epic/')) return `Epic #${params.id?.slice(0, 8) ?? ''}`
  if (location.pathname.startsWith('/issues/')) {
    return params.number ? `Issue #${params.number}` : 'Issue'
  }
  if (location.pathname === '/archived') return 'Archived'
  if (location.pathname.startsWith('/logs')) return 'Logs'
  if (location.pathname.startsWith('/settings')) {
    const section = params.section
    if (!section || section === 'ai') return 'Settings'
    return `Settings · ${section.charAt(0).toUpperCase()}${section.slice(1)}`
  }
  return 'Mohist'
}

export function Header({ onCreateIssue }: { onCreateIssue: () => void }) {
  const { isMobile } = useSidebar()
  const title = usePageTitle()
  const { data: agentStatus } = useAgentStatus()
  const running = agentStatus?.running ?? false

  return (
    <header className="h-12 shrink-0 flex items-center gap-2 border-b bg-background px-3 md:px-4">
      <SidebarTrigger className="-ml-1" />
      <div className="flex items-center gap-2 min-w-0">
        {isMobile && (
          <span className="text-sm font-bold tracking-tight">mohist</span>
        )}
        <h1 className="text-sm font-medium text-foreground truncate">{title}</h1>
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
        {!isMobile && (
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
