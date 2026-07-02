import type { ReactNode } from 'react'
import { InboxIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'

interface SectionStateProps {
  variant: 'loading' | 'error' | 'empty' | 'no-project'
  /** Title shown above the state. */
  title?: string
  /** Short description shown in the header; loading and empty use it for context. */
  description?: string
  /** Number of skeleton rows to render (loading only). */
  skeletonRows?: number
  /** Error message (error only). */
  message?: string
  /** Retry button (error only). */
  onRetry?: () => void
  /** Custom icon (empty only). */
  icon?: ReactNode
  /** Optional inline next-step action (empty + no-project only). */
  action?: ReactNode
  /** Optional override for empty/no-project body. */
  children?: ReactNode
  /** Optional test id forwarded to the outer container (useful for the no-project CTA). */
  'data-testid'?: string
}

export function SectionState({
  variant,
  title,
  description,
  skeletonRows = 4,
  message,
  onRetry,
  icon,
  action,
  children,
  'data-testid': testId,
}: SectionStateProps) {
  if (variant === 'loading') {
    return (
      <div className="space-y-4">
        {title && (
          <div>
            <h3 className="text-sm font-medium text-foreground">{title}</h3>
            {description && (
              <p className="text-xs text-muted-foreground mt-1">{description}</p>
            )}
          </div>
        )}
        <div
          role="status"
          aria-live="polite"
          className="rounded-lg border bg-card/50 p-4 space-y-3"
        >
          {Array.from({ length: skeletonRows }).map((_, i) => {
            const heights = ['h-3', 'h-3', 'h-4', 'h-3', 'h-3', 'h-4']
            const cls = heights[i % heights.length] ?? 'h-3'
            return (
              <div
                key={i}
                className={`${cls} w-${i % 3 === 0 ? 'full' : i % 3 === 1 ? '2/3' : '1/2'} bg-muted rounded animate-pulse`}
              />
            )
          })}
        </div>
      </div>
    )
  }

  if (variant === 'error') {
    return (
      <div className="space-y-4">
        {title && (
          <div>
            <h3 className="text-sm font-medium text-foreground">{title}</h3>
            {description && (
              <p className="text-xs text-muted-foreground mt-1">{description}</p>
            )}
          </div>
        )}
        <div className="rounded-md bg-red-50 border border-red-200 px-3 py-2 text-xs text-red-700">
          {message}
        </div>
        {onRetry && (
          <Button variant="outline" size="sm" onClick={onRetry}>
            Retry
          </Button>
        )}
      </div>
    )
  }

  // empty / no-project share the dashed-box layout; no-project just defaults a friendlier copy
  const isNoProject = variant === 'no-project'
  const defaultDescription = isNoProject
    ? 'Pick a project from the sidebar, or create a new one to get started.'
    : 'Nothing here yet.'
  const showAction = action != null || children != null

  return (
    <div className="space-y-4" data-testid={testId} data-variant={variant}>
      {title && (
        <div>
          <h3 className="text-sm font-medium text-foreground">{title}</h3>
          {description && (
            <p className="text-xs text-muted-foreground mt-1">{description}</p>
          )}
        </div>
      )}
      <div className="rounded-md border border-dashed bg-muted/30 p-8 text-center">
        <div className="mx-auto mb-2 inline-flex size-9 items-center justify-center rounded-full bg-muted text-muted-foreground/70">
          {icon ?? <InboxIcon className="size-4" />}
        </div>
        <p className="text-sm text-muted-foreground">{description ?? defaultDescription}</p>
        {showAction && (
          <div className="mt-4 flex flex-col items-center justify-center gap-2 sm:flex-row">
            {action}
            {children}
          </div>
        )}
      </div>
    </div>
  )
}
