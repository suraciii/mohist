import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { cn } from '@/shared/lib/utils'
import { AlertDialog } from '@/shared/ui/components/alert-dialog'
import {
  SETTINGS_SECTIONS,
  type SettingsSectionKey,
} from '../lib/sections'
import { useSettingsSectionPath } from '../lib/useSettingsSectionPath'
import { useRovingTabindex } from '../lib/useRovingTabindex'
import { useSettingsDirty } from '../lib/SettingsDirtyContext'

const SUBLINK_BASE =
  'flex items-center gap-2 rounded-md px-2 py-1 text-sm text-muted-foreground hover:text-foreground hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1'

export interface SettingsSubNavProps {
  activeSection: SettingsSectionKey
}

interface GroupData {
  scope: 'application' | 'project'
  heading: string
  items: { key: SettingsSectionKey; label: string; to: string | null; index: number }[]
}

interface SubNavItem {
  key: SettingsSectionKey
  label: string
  to: string | null
}

interface GroupNavProps {
  group: GroupData
  activeSection: SettingsSectionKey
  roving: ReturnType<typeof useRovingTabindex>
  onItemClick: (item: SubNavItem) => (event: MouseEvent<HTMLAnchorElement>) => void
}

function GroupNav({ group, activeSection, roving, onItemClick }: GroupNavProps) {
  return (
    <ul aria-label={group.heading} className="flex flex-col gap-0.5">
      {group.items.map((item) => {
        const isDisabled = item.to === null
        return (
          <li key={item.key}>
            <Link
              to={item.to ?? '#'}
              ref={roving.getItemRef(item.index)}
              tabIndex={roving.getItemTabIndex(item.index)}
              onKeyDown={roving.onKeyDown}
              onClick={onItemClick(item)}
              data-testid={`settings-subnav-${item.key}`}
              data-testid-scope={group.scope}
              aria-disabled={isDisabled ? 'true' : undefined}
              aria-current={item.key === activeSection ? 'page' : undefined}
              className={cn(
                SUBLINK_BASE,
                item.key === activeSection && 'bg-muted text-foreground font-medium',
                isDisabled && 'cursor-not-allowed opacity-50 hover:bg-transparent hover:text-muted-foreground',
              )}
            >
              {item.label}
            </Link>
          </li>
        )
      })}
    </ul>
  )
}

export function SettingsSubNav({ activeSection }: SettingsSubNavProps) {
  const sectionPath = useSettingsSectionPath()
  const navigate = useNavigate()
  const { dirty } = useSettingsDirty()
  const [pendingTarget, setPendingTarget] = useState<string | null>(null)
  const containerRef = useRef<HTMLDivElement | null>(null)
  const [isOverflowing, setIsOverflowing] = useState(false)
  const [showBottomFade, setShowBottomFade] = useState(false)
  const [showTopFade, setShowTopFade] = useState(false)
  const [rovingIndex, setRovingIndex] = useState(0)

  const { groups, flatItems } = useMemo(() => {
    const application: SubNavItem[] = []
    const project: SubNavItem[] = []
    for (const s of SETTINGS_SECTIONS) {
      const target = { key: s.key, label: s.label, to: sectionPath(s.key) }
      if (s.scope === 'application') {
        application.push(target)
      } else {
        project.push(target)
      }
    }
    const flatItems = [...application, ...project]
    const withIndex = (items: SubNavItem[]) =>
      items.map((item) => ({ ...item, index: flatItems.findIndex((candidate) => candidate.key === item.key) }))
    const result: GroupData[] = []
    if (application.length > 0) {
      result.push({ scope: 'application', heading: 'Application', items: withIndex(application) })
    }
    if (project.length > 0) {
      result.push({ scope: 'project', heading: 'Project', items: withIndex(project) })
    }
    return { groups: result, flatItems }
  }, [sectionPath])

  useEffect(() => {
    const activeIndex = flatItems.findIndex((item) => item.key === activeSection)
    setRovingIndex(activeIndex >= 0 ? activeIndex : 0)
  }, [flatItems, activeSection])

  const roving = useRovingTabindex({
    itemCount: flatItems.length,
    activeIndex: rovingIndex,
    onActivate: setRovingIndex,
  })

  useEffect(() => {
    if (rovingIndex >= flatItems.length) {
      setRovingIndex(Math.max(0, flatItems.length - 1))
    }
  }, [flatItems.length, rovingIndex])

  const handleItemClick = useCallback(
    (item: SubNavItem) => (event: MouseEvent<HTMLAnchorElement>) => {
      if (item.to === null) {
        event.preventDefault()
        return
      }
      if (!dirty || item.key === activeSection) return
      event.preventDefault()
      setPendingTarget(item.to)
    },
    [activeSection, dirty],
  )

  const handleDialogOpenChange = useCallback((next: boolean) => {
    if (!next) setPendingTarget(null)
  }, [])

  const handleConfirmDiscard = useCallback(() => {
    const target = pendingTarget
    setPendingTarget(null)
    if (target) navigate(target)
  }, [pendingTarget, navigate])

  useEffect(() => {
    const node = containerRef.current
    if (!node) return

    const measure = () => {
      const overflowing = node.scrollHeight > node.clientHeight
      setIsOverflowing(overflowing)
      if (overflowing) {
        const distFromBottom = node.scrollHeight - node.clientHeight - node.scrollTop
        const distFromTop = node.scrollTop
        setShowBottomFade(distFromBottom > 1)
        setShowTopFade(distFromTop > 1)
      } else {
        setShowBottomFade(false)
        setShowTopFade(false)
      }
    }

    measure()
    node.addEventListener('scroll', measure, { passive: true })

    let observer: ResizeObserver | null = null
    if (typeof ResizeObserver !== 'undefined') {
      observer = new ResizeObserver(() => measure())
      observer.observe(node)
    } else {
      window.addEventListener('resize', measure)
    }

    return () => {
      node.removeEventListener('scroll', measure)
      if (observer) {
        observer.disconnect()
      } else {
        window.removeEventListener('resize', measure)
      }
    }
  }, [])

  return (
    <div className="relative w-full md:w-56 md:shrink-0">
      <div
        ref={containerRef}
        className="settings-subnav-scroll flex max-h-[60vh] flex-col gap-4 overflow-y-auto md:max-h-none pr-2"
        data-testid="settings-subnav"
        data-overflow={isOverflowing ? 'overflowing' : 'contained'}
      >
        {groups.map((group) => (
          <div key={group.scope} data-testid={`settings-subnav-group-${group.scope}`}>
            <h2
              className={cn(
                'px-2 pb-1 text-xs font-semibold uppercase tracking-wide',
                group.scope === 'application'
                  ? 'text-primary/80'
                  : 'text-muted-foreground',
              )}
            >
              {group.heading}
            </h2>
            <GroupNav
              group={group}
              activeSection={activeSection}
              roving={roving}
              onItemClick={handleItemClick}
            />
          </div>
        ))}
      </div>
      {isOverflowing && (
        <div
          aria-hidden
          className="pointer-events-none absolute inset-x-0 top-0 h-6 bg-gradient-to-b from-background to-transparent transition-opacity"
          data-testid="settings-subnav-fade-top"
          data-visible={showTopFade ? 'true' : 'false'}
        />
      )}
      {isOverflowing && (
        <div
          aria-hidden
          className="pointer-events-none absolute inset-x-0 bottom-0 h-6 bg-gradient-to-t from-background to-transparent transition-opacity"
          data-testid="settings-subnav-fade-bottom"
          data-visible={showBottomFade ? 'true' : 'false'}
        />
      )}
      <AlertDialog
        open={pendingTarget !== null}
        onOpenChange={handleDialogOpenChange}
        title="Discard unsaved changes?"
        description="You have unsaved changes on this tab. Switching tabs will discard them."
        confirmLabel="Discard"
        cancelLabel="Stay"
        tone="destructive"
        onConfirm={handleConfirmDiscard}
        data-testid="settings-dirty-discard-alert"
      />
    </div>
  )
}
