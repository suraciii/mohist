import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { AlertTriangleIcon, CheckCircle2Icon, InfoIcon, WifiOffIcon, WifiIcon } from 'lucide-react'
import { cn } from '@/shared/lib/utils'

export type RuntimeToastTone = 'info' | 'success' | 'warning' | 'error' | 'transport'

export interface RuntimeToast {
  id: string
  tone: RuntimeToastTone
  title: string
  body?: string
  testId: string
  createdAt: number
  ttlMs: number
}

export interface PushRuntimeToastInput {
  tone: RuntimeToastTone
  title: string
  body?: string
  testId?: string
  ttlMs?: number
}

interface RuntimeToastContextValue {
  toasts: RuntimeToast[]
  push: (input: PushRuntimeToastInput) => string
  dismiss: (id: string) => void
  clear: () => void
}

const RuntimeToastContext = createContext<RuntimeToastContextValue | null>(null)
export { RuntimeToastContext }

export function useRuntimeToast(): RuntimeToastContextValue {
  const ctx = useContext(RuntimeToastContext)
  if (!ctx) {
    throw new Error('useRuntimeToast must be used within a RuntimeToastHost')
  }
  return ctx
}

const DEFAULT_TTL_MS = 6_000
const MAX_TOASTS = 6

function makeToastId(): string {
  return `t-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`
}

function tonePresentation(tone: RuntimeToastTone): {
  label: string
  borderClass: string
  bgClass: string
  titleClass: string
  bodyClass: string
  iconClass: string
  Icon: typeof InfoIcon
  testId: string
} {
  switch (tone) {
    case 'success':
      return {
        label: 'Success',
        borderClass: 'border-emerald-200',
        bgClass: 'bg-emerald-50',
        titleClass: 'text-emerald-900',
        bodyClass: 'text-emerald-800',
        iconClass: 'text-emerald-600',
        Icon: CheckCircle2Icon,
        testId: 'runtime-toast-success',
      }
    case 'warning':
      return {
        label: 'Warning',
        borderClass: 'border-amber-200',
        bgClass: 'bg-amber-50',
        titleClass: 'text-amber-900',
        bodyClass: 'text-amber-800',
        iconClass: 'text-amber-600',
        Icon: AlertTriangleIcon,
        testId: 'runtime-toast-warning',
      }
    case 'error':
      return {
        label: 'Error',
        borderClass: 'border-red-200',
        bgClass: 'bg-red-50',
        titleClass: 'text-red-900',
        bodyClass: 'text-red-800',
        iconClass: 'text-red-600',
        Icon: AlertTriangleIcon,
        testId: 'runtime-toast-error',
      }
    case 'transport':
      return {
        label: 'Connection',
        borderClass: 'border-slate-300',
        bgClass: 'bg-slate-50',
        titleClass: 'text-slate-900',
        bodyClass: 'text-slate-700',
        iconClass: 'text-slate-600',
        Icon: WifiOffIcon,
        testId: 'runtime-toast-transport',
      }
    case 'info':
    default:
      return {
        label: 'Info',
        borderClass: 'border-blue-200',
        bgClass: 'bg-blue-50',
        titleClass: 'text-blue-900',
        bodyClass: 'text-blue-800',
        iconClass: 'text-blue-600',
        Icon: InfoIcon,
        testId: 'runtime-toast-info',
      }
  }
}

function defaultTestIdFor(tone: RuntimeToastTone): string {
  if (tone === 'transport') return 'runtime-toast-transport'
  return tonePresentation(tone).testId
}

export interface RuntimeToastHostProps {
  children: ReactNode
  /**
   * Optional sink for the structured Activity surface. The host is purely
   * local; pages that own the Activity log can subscribe to mirror notices
   * out to the long-lived Activity stream without forcing the toast host to
   * know about Activity internals.
   */
  onNotice?: (notice: { toast: RuntimeToast }) => void
}

export function RuntimeToastHost({ children, onNotice }: RuntimeToastHostProps) {
  const [toasts, setToasts] = useState<RuntimeToast[]>([])
  const timers = useRef<Map<string, ReturnType<typeof setTimeout>>>(new Map())
  const onNoticeRef = useRef(onNotice)
  onNoticeRef.current = onNotice

  const clearTimers = useCallback(() => {
    for (const timer of timers.current.values()) {
      clearTimeout(timer)
    }
    timers.current.clear()
  }, [])

  useEffect(() => {
    return clearTimers
  }, [clearTimers])

  const dismiss = useCallback((id: string) => {
    setToasts((current) => current.filter((toast) => toast.id !== id))
    const timer = timers.current.get(id)
    if (timer) {
      clearTimeout(timer)
      timers.current.delete(id)
    }
  }, [])

  const clear = useCallback(() => {
    setToasts([])
    clearTimers()
  }, [clearTimers])

  const push = useCallback((input: PushRuntimeToastInput): string => {
    const id = makeToastId()
    const toast: RuntimeToast = {
      id,
      tone: input.tone,
      title: input.title,
      body: input.body,
      testId: input.testId ?? defaultTestIdFor(input.tone),
      createdAt: Date.now(),
      ttlMs: input.ttlMs ?? DEFAULT_TTL_MS,
    }
    setToasts((current) => {
      const next = [...current, toast]
      if (next.length > MAX_TOASTS) {
        return next.slice(next.length - MAX_TOASTS)
      }
      return next
    })
    if (toast.ttlMs > 0) {
      const timer = setTimeout(() => {
        setToasts((current) => current.filter((entry) => entry.id !== id))
        timers.current.delete(id)
      }, toast.ttlMs)
      timers.current.set(id, timer)
    }
    if (onNoticeRef.current) {
      onNoticeRef.current({ toast })
    }
    return id
  }, [])

  const value = useMemo<RuntimeToastContextValue>(
    () => ({ toasts, push, dismiss, clear }),
    [toasts, push, dismiss, clear],
  )

  return (
    <RuntimeToastContext.Provider value={value}>
      {children}
      <RuntimeToastViewport />
    </RuntimeToastContext.Provider>
  )
}

function RuntimeToastViewport() {
  const { toasts, dismiss } = useRuntimeToast()
  if (toasts.length === 0) return null
  return (
    <div
      data-testid="runtime-toast-host"
      role="region"
      aria-label="Runtime notifications"
      className="pointer-events-none fixed bottom-4 right-4 z-50 flex w-[22rem] max-w-[calc(100vw-2rem)] flex-col gap-2"
    >
      {toasts.map((toast) => {
        const presentation = tonePresentation(toast.tone)
        const Icon = presentation.Icon
        return (
          <div
            key={toast.id}
            data-testid={toast.testId}
            data-tone={toast.tone}
            role={toast.tone === 'error' ? 'alert' : 'status'}
            className={cn(
              'pointer-events-auto flex items-start gap-2 rounded-md border px-3 py-2 shadow-sm',
              presentation.borderClass,
              presentation.bgClass,
            )}
          >
            <Icon className={cn('mt-0.5 h-4 w-4 shrink-0', presentation.iconClass)} aria-hidden="true" />
            <div className="flex-1 min-w-0">
              <div
                data-testid="runtime-toast-title"
                className={cn('text-xs font-semibold uppercase tracking-wide', presentation.titleClass)}
              >
                {toast.title}
              </div>
              {toast.body && (
                <div data-testid="runtime-toast-body" className={cn('mt-0.5 text-xs', presentation.bodyClass)}>
                  {toast.body}
                </div>
              )}
            </div>
            <button
              type="button"
              onClick={() => dismiss(toast.id)}
              data-testid="runtime-toast-dismiss"
              aria-label="Dismiss notification"
              className={cn('text-xs underline', presentation.bodyClass)}
            >
              Dismiss
            </button>
          </div>
        )
      })}
    </div>
  )
}

export { WifiIcon as RuntimeToastConnectedIcon }
