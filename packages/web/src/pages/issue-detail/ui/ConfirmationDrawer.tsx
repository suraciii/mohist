import { useEffect, useRef, type ReactNode } from 'react'
import { cn } from '@/shared/lib/utils'

const FOCUSABLE_SELECTOR = [
  'button:not([disabled])',
  '[href]',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ')

function getFocusableElements(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR))
    .filter((element) => !element.hasAttribute('disabled') && element.tabIndex !== -1)
}

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
      const focusable = getFocusableElements(panel)[0]
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
        return
      }

      if (event.key !== 'Tab') return

      const panel = panelRef.current
      if (!panel) return
      const focusable = getFocusableElements(panel)
      if (focusable.length === 0) {
        event.preventDefault()
        panel.focus()
        return
      }

      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      const active = document.activeElement

      if (event.shiftKey) {
        if (active === first || !panel.contains(active)) {
          event.preventDefault()
          last.focus()
        }
        return
      }

      if (active === last || !panel.contains(active)) {
        event.preventDefault()
        first.focus()
      }
    }
    document.addEventListener('keydown', handleKeyDown)
    return () => document.removeEventListener('keydown', handleKeyDown)
  }, [open, onClose])

  useEffect(() => {
    if (!open) return
    const handleFocusIn = (event: FocusEvent) => {
      const panel = panelRef.current
      if (!panel || panel.contains(event.target as Node | null)) return
      const focusable = getFocusableElements(panel)
      ;(focusable[0] ?? panel).focus()
    }
    document.addEventListener('focusin', handleFocusIn)
    return () => document.removeEventListener('focusin', handleFocusIn)
  }, [open])

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
        'fixed inset-x-0 top-0 bottom-0 z-50 isolate flex items-end px-3 pb-3 pointer-events-auto bg-transparent',
      )}
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          event.preventDefault()
          event.stopPropagation()
        }
      }}
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
