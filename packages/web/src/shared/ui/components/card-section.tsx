import type { ComponentPropsWithoutRef, ReactNode } from 'react'
import { cn } from '@/shared/lib/utils'

export type CardSectionTone =
  | 'default'
  | 'amber'
  | 'red'
  | 'orange'
  | 'blue'
  | 'green'

interface CardSectionProps extends ComponentPropsWithoutRef<'section'> {
  title?: string
  icon?: ReactNode
  children: ReactNode
  tone?: CardSectionTone
  /** Override default heading element (e.g. for sub-sections in System). */
  titleAs?: 'h2' | 'h3' | 'h4'
}

const toneWrapper: Record<CardSectionTone, string> = {
  default: 'bg-card/50 border-border',
  amber: 'bg-warning-subtle border-warning-border',
  red: 'bg-danger-subtle border-danger-border',
  orange: 'bg-warning-subtle border-warning-border',
  blue: 'bg-info-subtle border-info-border',
  green: 'bg-success-subtle border-success-border',
}

const toneTitle: Record<CardSectionTone, string> = {
  default: 'text-muted-foreground',
  amber: 'text-warning',
  red: 'text-danger',
  orange: 'text-warning',
  blue: 'text-info',
  green: 'text-success',
}

export function CardSection({
  title,
  icon,
  children,
  tone = 'default',
  className,
  titleAs: TitleAs = 'h2',
  ...sectionProps
}: CardSectionProps) {
  return (
    <section {...sectionProps} className={cn('rounded-lg border p-4', toneWrapper[tone], className)}>
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
