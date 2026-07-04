import { useEffect, useRef, type ReactNode } from 'react'
import { cn } from '@/shared/lib/utils'

export interface ConfirmationDrawerProps {
  open: boolean
  onClose: () => void
  testId?: string
  titleId?: string
  descriptionId?: string
  children: ReactNode
}

export function ConfirmationDrawer({
  open,
  onClose,
  testId = 'confirmation-drawer',
  titleId,
  descriptionId,
  children,
}: ConfirmationDrawerProps) {
  const panelRef = useRef<HTMLDivElement | null>(null)
  const previousFocusRef = useRef<HTMLElement | null>(null)

  useEffect(() => {
    if (!open) return
    const active = document.activeElement
    if (active instanceof HTMLElement) {
      previousFocusRef.current = active
    }
    const panel = panelRef.current
    if (panel) {
      const focusable = panel.querySelector<HTMLElement>(
        'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])',
      )
      if (focusable) {
        focusable.focus()
      } else {
        panel.focus()
      }
    }
    return () => {
      previousFocusRef.current?.focus?.()
    }
  }, [open])

  useEffect(() => {
    if (!open) return
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.stopPropagation()
        onClose()
      }
    }
    document.addEventListener('keydown', handleKeyDown)
    return () => document.removeEventListener('keydown', handleKeyDown)
  }, [open, onClose])

  if (!open) return null

  return (
    <div
      data-testid={testId}
      data-state="open"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
      aria-describedby={descriptionId}
      className={cn(
        'fixed inset-x-0 bottom-0 z-50 isolate px-3 pb-3 pointer-events-none',
      )}
    >
      <div
        ref={panelRef}
        tabIndex={-1}
        className={cn(
          'pointer-events-auto mx-auto w-full max-w-md rounded-t-xl rounded-b-none border border-b-0 border-border bg-popover text-popover-foreground shadow-2xl ring-1 ring-foreground/10 outline-none',
          'translate-y-0 motion-safe:animate-in motion-safe:slide-in-from-bottom-full motion-safe:duration-200',
          'pb-[calc(0.75rem+env(safe-area-inset-bottom))]',
        )}
      >
        {children}
      </div>
    </div>
  )
}