import { useLocation, useNavigate } from 'react-router-dom'
import { ActivityIcon, ListTodoIcon, MenuIcon } from 'lucide-react'
import { useSidebar } from '@/shared/ui/components/sidebar'
import { Button } from '@/shared/ui/components/button'

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

  const tabs: Tab[] = [
    {
      label: 'Activity',
      path: '/activity',
      testId: 'mobile-nav-activity',
      icon: <ActivityIcon className="size-5" />,
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
    if (tab.path === '/') return location.pathname === '/'
    return location.pathname === tab.path || location.pathname.startsWith(`${tab.path}/`)
  }

  function handleClick(tab: Tab) {
    if (tab.action) {
      tab.action()
    } else {
      navigate(tab.path)
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
              {tab.icon}
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
