import { useLocation, useNavigate } from 'react-router-dom'
import { LayoutDashboardIcon, ListTodoIcon, ActivityIcon, MenuIcon, InboxIcon, BotIcon } from 'lucide-react'
import { useSidebar } from '@/shared/ui/components/sidebar'
import { Button } from '@/shared/ui/components/button'
import { useProjectPath } from '../../../entities/project'
import { useUnreadInboxCount } from '../../../entities/inbox'

interface Tab {
  label: string
  path: string
  icon: React.ReactNode
  action?: () => void
  testId: string
}

export function MobileBottomNav() {
  const location = useLocation()
  const navigate = useNavigate()
  const { setOpenMobile } = useSidebar()
  const toProjectPath = useProjectPath()
  const { data: unreadCount } = useUnreadInboxCount()

  const tabs: Tab[] = [
    {
      label: 'Dashboard',
      path: '/',
      testId: 'mobile-nav-dashboard',
      icon: <LayoutDashboardIcon className="size-5" />,
    },
    {
      label: 'Issues',
      path: '/issues',
      testId: 'mobile-nav-issues',
      icon: <ListTodoIcon className="size-5" />,
    },
    {
      label: 'Agents',
      path: '/agents',
      testId: 'mobile-nav-agents',
      icon: <BotIcon className="size-5" />,
    },
    {
      label: 'Activity',
      path: '/activity',
      testId: 'mobile-nav-activity',
      icon: <ActivityIcon className="size-5" />,
    },
    {
      label: 'Inbox',
      path: '/inbox',
      testId: 'mobile-nav-inbox',
      icon: <InboxIcon className="size-5" />,
    },
    {
      label: 'Epics',
      path: '/epics',
      testId: 'mobile-nav-epics',
      icon: <ListTodoIcon className="size-5" />,
    },
    {
      label: 'More',
      path: '__more__',
      testId: 'mobile-nav-more',
      icon: <MenuIcon className="size-5" />,
      action: () => setOpenMobile(true),
    },
  ]

  function isActive(tab: Tab) {
    if (tab.action) return false
    const path = toProjectPath(tab.path)
    if (path === '/') return location.pathname === '/'
    return location.pathname === path || location.pathname.startsWith(`${path}/`)
  }

  function handleClick(tab: Tab) {
    if (tab.action) {
      tab.action()
    } else {
      navigate(toProjectPath(tab.path))
    }
  }

  return (
    <nav
      className="fixed bottom-0 inset-x-0 md:hidden bg-background border-t z-40"
      style={{ paddingBottom: 'env(safe-area-inset-bottom, 0px)' }}
    >
      <div className="flex items-stretch h-14">
        {tabs.map((tab) => {
          const active = isActive(tab)
          return (
            <Button
              key={tab.path}
              variant="ghost"
              onClick={() => handleClick(tab)}
              data-testid={tab.testId}
              data-active={active}
              className={`flex-1 flex flex-col items-center justify-center gap-0.5 min-h-[44px] h-full rounded-none transition-colors ${
                active
                  ? 'text-blue-600'
                  : 'text-muted-foreground hover:text-foreground/80'
              }`}
            >
              <span className="relative">
                {tab.icon}
                {tab.testId === 'mobile-nav-inbox' && unreadCount != null && unreadCount > 0 && (
                  <span
                    data-testid="mobile-nav-inbox-badge"
                    className="absolute -top-1.5 -right-1.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-red-500 px-1 text-[10px] font-bold text-white tabular-nums leading-none"
                  >
                    {unreadCount > 99 ? '99+' : unreadCount}
                  </span>
                )}
              </span>
              <span className="text-[10px] font-medium leading-none">
                {tab.label}
              </span>
            </Button>
          )
        })}
      </div>
    </nav>
  )
}
