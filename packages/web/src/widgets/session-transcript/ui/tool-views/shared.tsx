import React from 'react'
import { getToolRegistryEntry } from '../tool-registry'
import {
  GENERIC_TOOL_LABEL,
  getFallbackSubtitle,
  getFilePathFromInput,
  getToolLabel,
  normalizeToolName,
  parseJsonSafely,
} from '../../model/transcript-tool-utils'

export function ToolIcon({ normalizedName }: { normalizedName: string }) {
  const entry = getToolRegistryEntry(normalizedName)
  const iconEl = entry.icon as React.ReactElement<{ className?: string; 'aria-hidden'?: boolean | 'true' | 'false' }>
  return React.cloneElement(iconEl, {
    className: 'h-3.5 w-3.5 text-muted-foreground/70 shrink-0',
    'aria-hidden': true,
  })
}

export function getToolDisplayLabel(normalizedName: string, displayTitle?: string, displaySubtitle?: string, rawInput?: string): string {
  if (displayTitle && displayTitle !== 'unknown') return displayTitle
  if (displaySubtitle && displaySubtitle !== 'unknown') return displaySubtitle
  const entry = getToolRegistryEntry(normalizedName)
  const registryTitle = entry.getTitle(normalizedName, rawInput)
  if (registryTitle && registryTitle !== 'unknown') return registryTitle
  if (normalizeToolName(normalizedName) !== 'unknown') return registryTitle
  return GENERIC_TOOL_LABEL
}

export function getToolDisplayArgs(normalizedName: string, rawInput?: string): string[] {
  const entry = getToolRegistryEntry(normalizedName)
  return entry.getBadges(normalizedName, rawInput)
}

export function getRegistrySubtitle(normalizedName: string, rawInput?: string): string | undefined {
  const entry = getToolRegistryEntry(normalizedName)
  return entry.getSubtitle(normalizedName, rawInput)
}

const EDIT_FAMILY = new Set(['edit', 'write', 'apply_patch'])
const READ_FAMILY = new Set(['read', 'read_file', 'glob', 'list', 'membrowse', 'memread'])
const SEARCH_FAMILY = new Set(['grep', 'search', 'search_files', 'websearch'])
const BASH_FAMILY = new Set(['bash', 'shell'])

function trimTarget(target: string): string {
  const trimmed = target.trim()
  if (trimmed.length <= 80) return trimmed
  return trimmed.slice(0, 77) + '…'
}

function extractFamilyTarget(normalizedName: string, rawInput?: string): { file?: string; command?: string; query?: string; path?: string } | null {
  const parsed = parseJsonSafely(rawInput)
  const filePath = getFilePathFromInput(rawInput)
  const fileNameFromPath = filePath ? filePath.split('/').pop() ?? filePath : undefined
  const nm = normalizedName.toLowerCase()
  if (EDIT_FAMILY.has(nm)) {
    const file = fileNameFromPath ?? (parsed && typeof parsed.filePath === 'string' ? parsed.filePath.split('/').pop() ?? parsed.filePath : undefined)
    if (file) return { file }
    return null
  }
  if (BASH_FAMILY.has(nm)) {
    const cmd = parsed ? parsed.command ?? parsed.script ?? parsed.cmd : undefined
    if (typeof cmd === 'string') return { command: cmd }
    return null
  }
  if (READ_FAMILY.has(nm)) {
    if (fileNameFromPath) return { path: fileNameFromPath }
    const label = getToolLabel(normalizedName, rawInput)
    if (label) return { path: label.split('/').pop() ?? label }
    return null
  }
  if (SEARCH_FAMILY.has(nm)) {
    const query = parsed ? parsed.query ?? parsed.pattern ?? parsed.search : undefined
    if (typeof query === 'string') return { query }
    return null
  }
  return null
}

export type VerbFamily = 'edit' | 'bash' | 'read' | 'search' | 'other'

export function deriveVerbFamily(normalizedName: string): VerbFamily {
  const nm = normalizedName.toLowerCase()
  if (EDIT_FAMILY.has(nm)) return 'edit'
  if (BASH_FAMILY.has(nm)) return 'bash'
  if (READ_FAMILY.has(nm)) return 'read'
  if (SEARCH_FAMILY.has(nm)) return 'search'
  return 'other'
}

export interface VerbLedTitle {
  verb: string
  family: VerbFamily
  target?: string
  trailingEllipsis: boolean
}

export function deriveVerbLedTitle(status: string, normalizedName: string, rawInput?: string, toolName?: string): VerbLedTitle {
  const family = deriveVerbFamily(normalizedName)
  const inFlight = status === 'running' || status === 'pending'
  const failed = status === 'failed'

  if (family === 'bash') {
    const target = extractFamilyTarget(normalizedName, rawInput)?.command
    const trailingEllipsis = inFlight && !target
    return {
      verb: '$',
      family,
      target: target ? trimTarget(target) : undefined,
      trailingEllipsis,
    }
  }

  if (family === 'edit') {
    const target = extractFamilyTarget(normalizedName, rawInput)?.file
    const trailingEllipsis = inFlight && !target
    let verb = 'Edited'
    if (inFlight) verb = 'Editing'
    else if (failed) verb = 'Failed to edit'
    return {
      verb,
      family,
      target: target ? trimTarget(target) : undefined,
      trailingEllipsis,
    }
  }

  if (family === 'read') {
    const target = extractFamilyTarget(normalizedName, rawInput)?.path
    const trailingEllipsis = inFlight && !target
    let verb = 'Read'
    if (inFlight) verb = 'Reading'
    else if (failed) verb = 'Failed to read'
    return {
      verb,
      family,
      target: target ? trimTarget(target) : undefined,
      trailingEllipsis,
    }
  }

  if (family === 'search') {
    const target = extractFamilyTarget(normalizedName, rawInput)?.query
    const trailingEllipsis = inFlight && !target
    let verb = 'Searched'
    if (inFlight) verb = 'Searching'
    else if (failed) verb = 'Failed to search'
    return {
      verb,
      family,
      target: target ? trimTarget(target) : undefined,
      trailingEllipsis,
    }
  }

  const fallback = normalizedName && normalizedName !== 'unknown' ? normalizedName : (toolName ?? normalizedName)
  const display = fallback && fallback !== 'unknown' ? fallback : GENERIC_TOOL_LABEL
  return {
    verb: inFlight ? `${display}…` : failed ? `${display} (failed)` : display,
    family,
    trailingEllipsis: inFlight,
  }
}

export function RunningIndicator() {
  return (
    <span className="relative flex h-2.5 w-2.5 shrink-0" data-tone="info" aria-hidden="true">
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
      return <span className="h-2 w-2 rounded-full bg-success shrink-0" data-tone="success" aria-hidden="true" />
    case 'failed':
      return <span className="h-2 w-2 rounded-full bg-danger shrink-0" data-tone="danger" aria-hidden="true" />
    case 'cancelled':
      return <span className="h-2 w-2 rounded-full bg-muted-foreground/60 shrink-0" data-tone="neutral" aria-hidden="true" />
    case 'pending':
    default:
      return <span className="h-2 w-2 rounded-full bg-muted-foreground/40 shrink-0" data-tone="neutral" aria-hidden="true" />
  }
}

export { getFallbackSubtitle }
