import React from 'react'
import { getToolRegistryEntry } from '../tool-registry'
import { getFallbackSubtitle } from '../../model/transcript-tool-utils'

export function ToolIcon({ normalizedName }: { normalizedName: string }) {
  const entry = getToolRegistryEntry(normalizedName)
  const iconEl = entry.icon as React.ReactElement<{ className?: string }>
  return React.cloneElement(iconEl, { className: 'h-3.5 w-3.5 text-gray-400 shrink-0' })
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
    <span className="relative flex h-2.5 w-2.5 shrink-0">
      <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75"></span>
      <span className="relative inline-flex rounded-full h-2.5 w-2.5 bg-blue-500"></span>
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
      return <span className="h-2 w-2 rounded-full bg-green-500 shrink-0" />
    case 'failed':
      return <span className="h-2 w-2 rounded-full bg-red-500 shrink-0" />
    case 'cancelled':
      return <span className="h-2 w-2 rounded-full bg-gray-400 shrink-0" />
    case 'pending':
    default:
      return <span className="h-2 w-2 rounded-full bg-gray-300 shrink-0" />
  }
}

export { getFallbackSubtitle }
