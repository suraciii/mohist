import type { ReactNode } from 'react'

interface SettingsSectionProps {
  title: string
  description?: string
  children: ReactNode
}

export function SettingsSection({
  title,
  description,
  children,
}: SettingsSectionProps) {
  return (
    <section>
      <h2 className="text-sm font-medium text-foreground">{title}</h2>
      {description && <p className="mt-1 text-sm text-foreground/85">{description}</p>}
      <div className="mt-4 space-y-4">{children}</div>
    </section>
  )
}
