import { useState } from 'react'
import { Button } from '@/shared/ui/components/button'
import type { DisplayAssistantPart, DisplayToolPart, DisplayChangedFile } from '../../model/session-transcript-display'
import { formatElapsed } from '../../model/format-duration'
import { getDisplayType, parseEditInput, parseEditWriteChanges, parsePatchOperations } from '../../model/transcript-tool-utils'
import { BashContentView } from './bash-view'
import { ReadContentView } from './read-view'
import { SearchContentView } from './search-view'
import { TodoContentView } from './todo-view'
import { DelegationContentView } from './delegation-view'
import { DiffContentView } from './diff-view'
import {
  ToolStatusDot,
  deriveVerbFamily,
  deriveVerbLedTitle,
  getToolDisplayArgs,
} from './shared'

export { BashContentView } from './bash-view'
export { ReadContentView } from './read-view'
export { SearchContentView } from './search-view'
export { TodoContentView } from './todo-view'
export { DelegationContentView } from './delegation-view'
export { DiffContentView, PatchDiffView } from './diff-view'
export {
  ToolIcon,
  ToolStatusDot,
  truncateOutput,
  getToolDisplayLabel,
  getToolDisplayArgs,
  getRegistrySubtitle,
  getFallbackSubtitle,
  deriveVerbFamily,
  deriveVerbLedTitle,
  type VerbFamily,
  type VerbLedTitle,
} from './shared'

interface EditInlineStats {
  singleFile: DisplayChangedFile | null
  additions: number | undefined
  deletions: number | undefined
  fileCount: number
}

function buildEditInlineStats(part: DisplayToolPart): EditInlineStats {
  const files = part.changedFiles
  if (files && files.length > 0) {
    const additions = files.reduce<number | undefined>((acc, f) => {
      if (typeof f.additions === 'number') return (acc ?? 0) + f.additions
      return acc
    }, undefined)
    const deletions = files.reduce<number | undefined>((acc, f) => {
      if (typeof f.deletions === 'number') return (acc ?? 0) + f.deletions
      return acc
    }, undefined)
    return {
      singleFile: files.length === 1 ? files[0] : null,
      additions,
      deletions,
      fileCount: files.length,
    }
  }

  if (part.normalizedName !== 'apply_patch' && part.input) {
    const parsed = parseEditInput(part.input)
    if (parsed) {
      const derived = parseEditWriteChanges(parsed)
      if (derived.length > 0) {
        return {
          singleFile: derived.length === 1 ? derived[0] : null,
          additions: derived.reduce<number | undefined>((acc, f) => typeof f.additions === 'number' ? (acc ?? 0) + f.additions : acc, undefined),
          deletions: derived.reduce<number | undefined>((acc, f) => typeof f.deletions === 'number' ? (acc ?? 0) + f.deletions : acc, undefined),
          fileCount: derived.length,
        }
      }
    }
  }

  if (part.normalizedName === 'apply_patch' && part.input) {
    let patchText: string | undefined
    try {
      const obj = JSON.parse(part.input)
      patchText = obj?.patchText ?? obj?.patch
    } catch {
      patchText = part.input
    }
    if (patchText && patchText.includes('*** ')) {
      const derived = parsePatchOperations(patchText)
      if (derived.length > 0) {
        return {
          singleFile: derived.length === 1 ? derived[0] : null,
          additions: derived.reduce<number | undefined>((acc, f) => typeof f.additions === 'number' ? (acc ?? 0) + f.additions : acc, undefined),
          deletions: derived.reduce<number | undefined>((acc, f) => typeof f.deletions === 'number' ? (acc ?? 0) + f.deletions : acc, undefined),
          fileCount: derived.length,
        }
      }
    }
  }

  return { singleFile: null, additions: undefined, deletions: undefined, fileCount: 0 }
}

function deriveChangedFilesForExpand(part: DisplayToolPart): DisplayChangedFile[] {
  if (part.changedFiles && part.changedFiles.length > 0) return part.changedFiles
  const stats = buildEditInlineStats(part)
  if (stats.singleFile) return [stats.singleFile]
  if (part.input) {
    const parsed = parseEditInput(part.input)
    if (parsed) return parseEditWriteChanges(parsed)
  }
  return []
}

interface ToolRowViewProps {
  part: Extract<DisplayAssistantPart, { partType: 'tool' }>
}

export function ToolRowView({ part }: ToolRowViewProps) {
  const [expanded, setExpanded] = useState(false)
  const isRunning = part.status === 'running' || part.status === 'pending'
  const isFailed = part.status === 'failed'
  const family = deriveVerbFamily(part.normalizedName)
  const isEditFamily = family === 'edit'
  const editStats = isEditFamily ? buildEditInlineStats(part) : null
  const verbTitle = deriveVerbLedTitle(part.status, part.normalizedName, part.input, part.toolName)
  const toolArgs = getToolDisplayArgs(part.normalizedName, part.input)

  const duration = formatElapsed(part.startedAt, part.completedAt ?? undefined)

  const derivedChangedFiles = isEditFamily ? deriveChangedFilesForExpand(part) : []
  const hasExpandableDetail =
    !!part.input ||
    !!part.output ||
    !!part.error ||
    (isEditFamily && derivedChangedFiles.length > 0)
  const expandable = !isRunning && hasExpandableDetail

  const displayType = getDisplayType(part.normalizedName)

  const renderExpandedContent = () => {
    if (part.error) {
      return (
        <div
          data-testid="tool-row-error"
          data-tone="danger"
          className="border-t border-danger-border/40 px-3 py-2 text-xs text-danger bg-danger-subtle/40"
        >
          {part.error}
        </div>
      )
    }

    if (displayType === 'terminal' && (part.input || part.output)) {
      return <BashContentView input={part.input} output={part.output} details={part.details} />
    }

    if ((part.normalizedName === 'read' || part.normalizedName === 'read_file') && (part.input || part.output)) {
      return (
        <>
          <ReadContentView input={part.input} output={part.output} />
          {part.input && (
            <div className="px-3 pb-2">
              <div className="font-medium text-xs text-muted-foreground mb-1">Input</div>
              <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-muted-foreground bg-muted rounded p-2 max-h-24 overflow-auto">
                {part.input}
              </pre>
            </div>
          )}
        </>
      )
    }

    if ((part.normalizedName === 'grep' || part.normalizedName === 'search' || part.normalizedName === 'search_files') && (part.input || part.output)) {
      return <SearchContentView input={part.input} output={part.output} />
    }

    if ((part.normalizedName === 'todowrite' || part.normalizedName === 'todo') && part.input) {
      return <TodoContentView input={part.input} />
    }

    if (part.normalizedName === 'task' && part.details) {
      return <DelegationContentView input={part.input} details={part.details} />
    }

    if (isEditFamily) {
      return (
        <DiffContentView
          changedFiles={derivedChangedFiles}
          rawInput={part.rawInput}
          rawOutput={part.rawOutput}
          details={part.details}
          normalizedName={part.normalizedName}
        />
      )
    }

    return (
      <>
        {part.input && (
          <div className="border-t border-border/50 px-3 pt-2">
            <div className="font-medium text-xs text-muted-foreground mb-1">Input</div>
            <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-muted-foreground bg-muted rounded p-2 max-h-32 overflow-auto">
              {part.input}
            </pre>
          </div>
        )}
        {part.output && (
          <div className="border-t border-border/50 px-3 py-2">
            <div className="font-medium text-xs text-muted-foreground mb-1">Output</div>
            <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-muted-foreground bg-muted rounded p-2 max-h-32 overflow-auto">
              {part.output}
            </pre>
          </div>
        )}
      </>
    )
  }

  const rowClass = isFailed
    ? 'flex flex-wrap items-center gap-x-2 gap-y-1 px-2 py-1.5 min-w-0 bg-danger-subtle/40 text-danger rounded-sm'
    : 'flex flex-wrap items-center gap-x-2 gap-y-1 px-2 py-1.5 min-w-0 text-foreground/90 hover:bg-muted/40 rounded-sm transition-colors'

  return (
    <div
      data-testid="tool-row"
      data-tone={isFailed ? 'danger' : part.status === 'completed' ? 'success' : isRunning ? 'info' : 'neutral'}
      data-tool-call-id={part.toolCallId}
      data-tool-state={part.status}
      className="w-full min-w-0"
    >
      <Button
        variant="ghost"
        size="sm"
        onClick={expandable ? () => setExpanded(!expanded) : undefined}
        aria-expanded={expandable ? expanded : undefined}
        className={`${rowClass} h-auto w-full text-left rounded-sm ${expandable ? 'cursor-pointer' : 'cursor-default'}`}
      >
        <ToolStatusDot status={part.status} />
        <span
          data-testid="tool-row-verb-title"
          className={`text-xs font-medium shrink-0 ${isFailed ? 'text-danger' : 'text-foreground'}`}
        >
          {verbTitle.verb}
          {verbTitle.trailingEllipsis ? '' : verbTitle.target ? ` ${verbTitle.target}` : ''}
          {verbTitle.trailingEllipsis ? '…' : ''}
        </span>
        {toolArgs.length > 0 && !verbTitle.target && (
          <span className="flex gap-1 shrink-0">
            {toolArgs.slice(0, 2).map((arg, i) => (
              <span
                key={i}
                className={`inline-flex items-center px-1 py-0.5 rounded text-xs font-mono ${
                  isFailed
                    ? 'bg-danger/15 text-danger/80'
                    : 'bg-muted text-muted-foreground'
                }`}
              >
                {arg}
              </span>
            ))}
          </span>
        )}
        {editStats && editStats.singleFile && (
          <span
            data-testid="tool-row-edit-file"
            className={`text-xs font-mono truncate min-w-0 max-w-[40ch] ${isFailed ? 'text-danger/80' : 'text-foreground/70'}`}
            title={editStats.singleFile.path}
          >
            {editStats.singleFile.path}
          </span>
        )}
        {editStats && editStats.singleFile && (
          <span
            data-testid="tool-row-edit-stats"
            className="flex gap-1.5 shrink-0 text-xs font-mono"
          >
            {typeof editStats.additions === 'number' && (
              <span className="text-success">+{editStats.additions}</span>
            )}
            {typeof editStats.deletions === 'number' && (
              <span className="text-danger">−{editStats.deletions}</span>
            )}
          </span>
        )}
        {editStats && !editStats.singleFile && editStats.fileCount > 1 && (
          <span
            data-testid="tool-row-edit-file-count"
            className={`text-xs ${isFailed ? 'text-danger/80' : 'text-muted-foreground'}`}
          >
            {editStats.fileCount} files
          </span>
        )}
        {duration && !isRunning && (
          <span
            data-testid="tool-row-duration"
            className={`ml-auto text-xs shrink-0 tabular-nums ${isFailed ? 'text-danger/70' : 'text-muted-foreground/70'}`}
          >
            {duration}
          </span>
        )}
        {!expandable && (
          <span aria-hidden="true" className="ml-auto" />
        )}
        {expandable && (
          <svg
            aria-hidden="true"
            className={`h-3 w-3 text-muted-foreground/60 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''} ${duration || editStats ? '' : 'ml-auto'}`}
            viewBox="0 0 20 20"
            fill="currentColor"
          >
            <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
          </svg>
        )}
      </Button>
      {expanded && expandable && (
        <div data-testid="tool-row-detail" className="min-w-0 mt-1">
          {renderExpandedContent()}
        </div>
      )}
    </div>
  )
}

interface ContextGroupViewProps {
  title: string
  tools: Extract<DisplayAssistantPart, { partType: 'tool' }>[]
  hasError: boolean
}

export function ContextGroupView({ title, tools, hasError }: ContextGroupViewProps) {
  const [expanded, setExpanded] = useState(false)
  const titleSegments = title.split(' · ')
  const titlePrefix = titleSegments[0]
  const titleDetail = titleSegments.length > 1 ? titleSegments.slice(1).join(' · ') : null

  const rowClass = hasError
    ? 'flex flex-wrap items-center gap-x-2 gap-y-1 px-2 py-1.5 min-w-0 bg-danger-subtle/40 text-danger rounded-sm hover:bg-danger-subtle/60'
    : 'flex flex-wrap items-center gap-x-2 gap-y-1 px-2 py-1.5 min-w-0 text-foreground/90 hover:bg-muted/40 rounded-sm transition-colors'

  return (
    <div
      className="w-full min-w-0"
      data-testid="context-group-row"
      data-tone={hasError ? 'danger' : 'neutral'}
    >
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        aria-expanded={expanded}
        className={`${rowClass} h-auto w-full text-left rounded-sm cursor-pointer`}
      >
        <span
          aria-hidden="true"
          className={`h-2 w-2 rounded-full shrink-0 ${hasError ? 'bg-danger' : 'bg-info'}`}
        />
        <span
          data-testid="context-group-summary-prefix"
          className={`text-xs font-medium shrink-0 ${hasError ? 'text-danger' : 'text-foreground'}`}
        >
          {titlePrefix}
        </span>
        {titleDetail && (
          <span
            data-testid="context-group-summary-detail"
            className={`text-xs truncate min-w-0 max-w-[40ch] ${hasError ? 'text-danger/80' : 'text-muted-foreground'}`}
          >
            {titleDetail}
          </span>
        )}
        {hasError && (
          <span
            data-testid="context-group-failed-label"
            data-tone="danger"
            className="text-xs text-danger"
          >
            failed
          </span>
        )}
        <svg
          aria-hidden="true"
          className={`h-3 w-3 text-muted-foreground/60 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`}
          viewBox="0 0 20 20"
          fill="currentColor"
        >
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
      </Button>
      {expanded && (
        <div
          data-testid="context-group-children"
          className="min-w-0 mt-1 space-y-0.5"
        >
          {tools.map((tool) => (
            <ToolRowView key={tool.id} part={tool} />
          ))}
        </div>
      )}
    </div>
  )
}
