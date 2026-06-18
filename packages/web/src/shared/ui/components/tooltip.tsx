import { useId, useState, type ReactNode } from 'react'

import { cn } from '@/shared/lib/utils'

interface TooltipProps {
  content: ReactNode
  children: ReactNode
  className?: string
}

export function Tooltip({ content, children, className }: TooltipProps) {
  const id = useId()
  const [open, setOpen] = useState(false)

  return (
    <span className={cn('relative inline-flex items-center', className)}>
      <span
        tabIndex={0}
        aria-describedby={open ? id : undefined}
        onMouseEnter={() => setOpen(true)}
        onMouseLeave={() => setOpen(false)}
        onFocus={() => setOpen(true)}
        onBlur={() => setOpen(false)}
        className="cursor-help rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
      >
        {children}
      </span>
      {open && (
        <span
          id={id}
          role="tooltip"
          className="absolute bottom-full left-0 z-50 mb-2 w-64 rounded-md border bg-popover px-3 py-2 text-xs font-normal leading-relaxed text-popover-foreground shadow-md"
        >
          {content}
        </span>
      )}
    </span>
  )
}
