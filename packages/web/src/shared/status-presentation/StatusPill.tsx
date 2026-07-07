import type { ReactNode } from 'react'
import { cn } from '@/shared/lib/utils'
import { statusTreatment, type StatusKind, type StatusTreatment } from './index'

export interface StatusPillProps {
  kind: StatusKind
  state: string | null | undefined
  className?: string
  children?: ReactNode
  /**
   * Optional leading icon. Callers typically pass one of the resolved
   * `StageStatusIcons` (`CheckmarkIcon`, `SpinnerIcon`, …). The pill does not
   * pick icons itself; icon mapping is a product concern.
   */
  icon?: ReactNode
  /**
   * Show a small leading dot whose fill matches the pill family. Defaults to
   * false — call sites that need a dot opt in.
   */
  withDot?: boolean
  /**
   * Test id forwarded to the rendered element. Use this to disambiguate
   * multiple pills on the same surface.
   */
  testId?: string
}

/**
 * Thin pill that renders `statusTreatment(...)` directly.
 *
 * The treatment supplies the entire color set (background, text, border, dot)
 * — no Badge primitive sits between, because every Badge variant in the app
 * still uses `text-<family>-foreground` (page foreground) which has poor
 * contrast against the soft-tinted backgrounds. Calling sites that want
 * layout primitives (size, padding, radius) can wrap or compose the pill.
 *
 * The optional dot inherits the same family as the pill text/background so
 * dot, text, and container cannot disagree.
 */
export function StatusPill({
  kind,
  state,
  className,
  children,
  icon,
  withDot = false,
  testId,
}: StatusPillProps) {
  const treatment: StatusTreatment = statusTreatment(kind, state)
  return (
    <span
      data-testid={testId}
      data-status={state ?? 'unknown'}
      data-family={treatment.family}
      data-slot="status-pill"
      className={cn(
        'inline-flex h-5 w-fit shrink-0 items-center gap-1 overflow-hidden rounded-4xl border px-2 py-0.5 text-xs font-medium whitespace-nowrap',
        treatment.container,
        treatment.border,
        className,
      )}
    >
      {icon}
      {withDot && (
        <span
          aria-hidden="true"
          className={cn('inline-block h-1.5 w-1.5 shrink-0 rounded-full', treatment.dot)}
        />
      )}
      {children}
    </span>
  )
}