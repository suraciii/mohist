import React from 'react'
import { getToolRegistryEntry } from '../tool-registry'
import { getFallbackSubtitle } from '../../model/transcript-tool-utils'

export function ToolIcon({ normalizedName }: { normalizedName: string }) {
  const entry = getToolRegistryEntry(normalizedName)
  const iconEl = entry.icon as React.ReactElement<{ className?: string }>
  return React.cloneElement(iconEl, { className: 'h-3.5 w-3.5 text-muted-foreground/70 shrink-0' })
}

export function getToolDisplayLabel(normalizedName: string, displayTitle?: string, displaySubtitle?: string, rawInput?: string): string {
  if (displayTitle) return displayTitle
  if (displaySubtitle) return displaySubtitle
  const entry = getToolRegistryEntry(normalizedName)
  return entry.getTitle(normalizedName, rawInput)
}

export function getToolDisplayArgs(normalizedName: string, rawInput?: string): string[] {
  const entry = getToolRegistryEntry(normalizedName)
  return entry.getBadges(normalizedName, rawInput)
}

export function getRegistrySubtitle(normalizedName: string, rawInput?: string): string | undefined {
  const entry = getToolRegistryEntry(normalizedName)
  return entry.getSubtitle(normalizedName, rawInput)
}

export function RunningIndicator() {
  return (
    <span className="relative flex h-2.5 w-2.5 shrink-0" data-tone="info">
      <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-info/70 opacity-75"></span>
      <span className="relative inline-flex rounded-full h-2.5 w-2.5 bg-info"></span>
    </span>
  )
}

export function truncateOutput(output: string, maxLines: number = 5): string {
  const lines = output.split('\n')
  if (lines.length <= maxLines) return output
  return lines.slice(0, maxLines).join('\n') + '\n...'
}

interface ToolStatusDotProps {
  status: string
}

export function ToolStatusDot({ status }: ToolStatusDotProps) {
  switch (status) {
    case 'running':
      return <RunningIndicator />
    case 'completed':
      return <span className="h-2 w-2 rounded-full bg-success shrink-0" data-tone="success" />
    case 'failed':
      return <span className="h-2 w-2 rounded-full bg-danger shrink-0" data-tone="danger" />
    case 'cancelled':
      return <span className="h-2 w-2 rounded-full bg-muted-foreground/60 shrink-0" data-tone="neutral" />
    case 'pending':
    default:
      return <span className="h-2 w-2 rounded-full bg-muted-foreground/40 shrink-0" data-tone="neutral" />
  }
}

export { getFallbackSubtitle }
