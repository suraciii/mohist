import { ChevronDownIcon, ChevronRightIcon } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { cn } from '@/shared/lib/utils'

export interface CollapsibleRailCardProps {
  title: string
  testId: string
  defaultCollapsed?: boolean
  forceCollapsed?: boolean
  children: ReactNode
  summary?: ReactNode
}

export function CollapsibleRailCard({
  title,
  testId,
  defaultCollapsed = false,
  forceCollapsed = false,
  summary,
  children,
}: CollapsibleRailCardProps) {
  const [expanded, setExpanded] = useState<boolean>(!(defaultCollapsed || forceCollapsed))

  useEffect(() => {
    if (forceCollapsed) {
      setExpanded(false)
    }
  }, [forceCollapsed])

  const collapsed = !expanded

  return (
    <section
      data-testid={testId}
      data-collapsed={collapsed ? 'true' : 'false'}
      data-rail-card="collapsible"
      className={cn(
        'rounded-lg border border-border bg-card/50',
      )}
    >
      <button
        type="button"
        onClick={() => setExpanded((value) => !value)}
        aria-expanded={!collapsed}
        aria-controls={`${testId}-body`}
        data-testid={`${testId}-toggle`}
        className={cn(
          'flex w-full min-w-0 items-center gap-2 text-left font-semibold uppercase tracking-wide text-muted-foreground transition-colors',
          collapsed
            ? 'rounded-md px-3 py-2 text-[10px] hover:bg-card/70'
            : 'rounded-t-md px-4 py-3 text-xs border-b border-border/40 hover:bg-card/60',
        )}
      >
        {collapsed ? (
          <ChevronRightIcon className="size-3.5 shrink-0" aria-hidden="true" />
        ) : (
          <ChevronDownIcon className="size-3.5 shrink-0" aria-hidden="true" />
        )}
        <span className="min-w-0 flex-1 break-words text-balance">{title}</span>
        {collapsed && summary && (
          <span
            data-testid={`${testId}-summary`}
            className="ml-2 truncate text-[10px] font-normal normal-case tracking-normal text-muted-foreground/70"
          >
            {summary}
          </span>
        )}
      </button>
      {!collapsed && (
        <div
          id={`${testId}-body`}
          data-testid={`${testId}-body`}
          className="p-4"
        >
          {children}
        </div>
      )}
    </section>
  )
}
