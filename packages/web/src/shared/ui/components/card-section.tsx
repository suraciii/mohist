import type { ReactNode } from 'react'
import { cn } from '@/shared/lib/utils'

export type CardSectionTone =
  | 'default'
  | 'amber'
  | 'red'
  | 'orange'
  | 'blue'
  | 'green'

interface CardSectionProps {
  title?: string
  icon?: ReactNode
  children: ReactNode
  tone?: CardSectionTone
  className?: string
  /** Override default heading element (e.g. for sub-sections in System). */
  titleAs?: 'h2' | 'h3' | 'h4'
}

const toneWrapper: Record<CardSectionTone, string> = {
  default: 'bg-card/50 border-border',
  amber: 'bg-amber-50 border-amber-200',
  red: 'bg-red-50 border-red-200',
  orange: 'bg-orange-50 border-orange-200',
  blue: 'bg-blue-50 border-blue-200',
  green: 'bg-green-50 border-green-200',
}

const toneTitle: Record<CardSectionTone, string> = {
  default: 'text-muted-foreground',
  amber: 'text-amber-800',
  red: 'text-red-800',
  orange: 'text-orange-800',
  blue: 'text-blue-800',
  green: 'text-green-800',
}

export function CardSection({
  title,
  icon,
  children,
  tone = 'default',
  className,
  titleAs: TitleAs = 'h2',
}: CardSectionProps) {
  return (
    <section className={cn('rounded-lg border p-4', toneWrapper[tone], className)}>
      {title && (
        <TitleAs
          className={cn(
            'text-xs font-semibold uppercase tracking-wide mb-3 flex items-center gap-1.5',
            toneTitle[tone],
          )}
        >
          {icon}
          {title}
        </TitleAs>
      )}
      {children}
    </section>
  )
}
