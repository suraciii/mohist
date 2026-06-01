import type { ReactNode } from 'react'
import { InboxIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'

interface SectionStateProps {
  variant: 'loading' | 'error' | 'empty'
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
  /** Optional override for empty-state body. */
  children?: ReactNode
}

export function SectionState({
  variant,
  title,
  description,
  skeletonRows = 4,
  message,
  onRetry,
  icon,
  children,
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

  // empty
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
      <div className="rounded-md border border-dashed bg-muted/30 p-8 text-center">
        <div className="mx-auto mb-2 inline-flex size-9 items-center justify-center rounded-full bg-muted text-muted-foreground/70">
          {icon ?? <InboxIcon className="size-4" />}
        </div>
        <p className="text-sm text-muted-foreground">{description ?? 'Nothing here yet.'}</p>
        {children}
      </div>
    </div>
  )
}
