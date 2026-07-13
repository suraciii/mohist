import { useState } from 'react'
import type { JSX } from 'react'
import { Button } from '@/shared/ui/components/button'
import type { ToolCallEntry, FileChangeSummary } from '../../../entities/coder-session'
import {
  getToolLabel,
  getToolArgs,
  ToolDisplayType,
  parsePatchOperations,
  parseEditInput,
  parseEditWriteChanges,
  getDisplayType,
  type EditInput,
} from '../model/transcript-tool-utils'

export { getToolLabel, getToolArgs, parsePatchOperations, parseEditInput }
export type { ToolDisplayType, EditInput }

export interface BashInput {
  command: string
}

function PatchBlock({ patch }: { patch: string }) {
  const lines = patch.split('\n')
  const COLLAPSE_THRESHOLD = 80
  const visibleLines = lines.slice(0, COLLAPSE_THRESHOLD)
  const isCollapsed = lines.length > COLLAPSE_THRESHOLD

  return (
    <div className="text-xs font-mono rounded overflow-hidden border border-gray-200">
      {isCollapsed && (
        <div className="bg-gray-50 px-3 py-1.5 text-gray-500 border-b border-gray-100">
          {lines.length} patch lines · showing first {COLLAPSE_THRESHOLD}
        </div>
      )}
      <div className="max-h-64 overflow-auto">
        {visibleLines.map((line, i) => {
          const className = line.startsWith('+') && !line.startsWith('+++')
            ? 'bg-green-50 text-green-800 border-l-2 border-green-400'
            : line.startsWith('-') && !line.startsWith('---')
              ? 'bg-red-50 text-red-800 border-l-2 border-red-400'
              : 'bg-gray-50 text-gray-700 border-l-2 border-gray-200'
          return (
            <div key={i} className={`${className} px-3 py-0.5 min-h-[1.25rem] whitespace-pre-wrap break-all`}>
              {line || ' '}
            </div>
          )
        })}
      </div>
    </div>
  )
}

export function parseBashInput(rawInput: string | undefined): BashInput | null {
  if (!rawInput) return null
  try {
    const parsed = JSON.parse(rawInput)
    if (typeof parsed !== 'object' || parsed === null) return null
    const command = parsed.command ?? parsed.script ?? ''
    return { command }
  } catch {
    return null
  }
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`
}

function stripAnsi(text: string): string {
  return text.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, '')
}

function tryFormatJson(text: string): string {
  try {
    const parsed = JSON.parse(text)
    return JSON.stringify(parsed, null, 2)
  } catch {
    return text
  }
}

function DiffBlock({ oldStr, newStr }: { oldStr: string; newStr: string }) {
  const oldLines = oldStr ? oldStr.split('\n') : []
  const newLines = newStr ? newStr.split('\n') : []
  const totalLines = oldLines.length + newLines.length
  const COLLAPSE_THRESHOLD = 20
  const isLarge = totalLines > COLLAPSE_THRESHOLD

  if (isLarge) {
    return (
      <div className="text-xs font-mono rounded overflow-hidden border border-gray-200">
        <div className="bg-gray-50 px-3 py-1.5 text-gray-500 border-b border-gray-100">
          {oldLines.length > 0 && (
            <span className="text-red-500 mr-3">-{oldLines.length} lines</span>
          )}
          {newLines.length > 0 && (
            <span className="text-green-600">+{newLines.length} lines</span>
          )}
        </div>
        <div className="max-h-48 overflow-auto">
          {oldLines.length > 0 && oldLines.map((line, i) => (
            <div key={`old-${i}`} className="bg-red-50 text-red-800 px-3 py-0.5 border-l-2 border-red-400 min-h-[1.25rem]">
              <span className="text-red-400 select-none mr-2">-</span>{line}
            </div>
          ))}
          {newLines.length > 0 && newLines.map((line, i) => (
            <div key={`new-${i}`} className="bg-green-50 text-green-800 px-3 py-0.5 border-l-2 border-green-400 min-h-[1.25rem]">
              <span className="text-green-400 select-none mr-2">+</span>{line}
            </div>
          ))}
        </div>
      </div>
    )
  }

  return (
    <div className="text-xs font-mono rounded overflow-hidden border border-gray-200">
      {oldLines.length > 0 && oldLines.map((line, i) => (
        <div key={`old-${i}`} className="bg-red-50 text-red-800 px-3 py-0.5 border-l-2 border-red-400 min-h-[1.25rem]">
          <span className="text-red-400 select-none mr-2">-</span>{line}
        </div>
      ))}
      {newLines.length > 0 && newLines.map((line, i) => (
        <div key={`new-${i}`} className="bg-green-50 text-green-800 px-3 py-0.5 border-l-2 border-green-400 min-h-[1.25rem]">
          <span className="text-green-400 select-none mr-2">+</span>{line}
        </div>
      ))}
    </div>
  )
}

function TerminalBlock({ output, collapsed }: { output: string; collapsed: boolean }) {
  const cleanOutput = stripAnsi(output)
  const lines = cleanOutput.split('\n')
  const COLLAPSE_THRESHOLD = 20
  const shouldCollapse = !collapsed && lines.length > COLLAPSE_THRESHOLD
  const sizeKB = (new Blob([cleanOutput]).size / 1024).toFixed(1)

  if (shouldCollapse) {
    return (
      <div className="text-xs font-mono rounded bg-gray-900 text-gray-100 overflow-hidden">
        <div className="px-3 py-1.5 text-gray-400 border-b border-gray-700">
          {lines.length} lines · {sizeKB}KB
        </div>
        <pre className="p-3 whitespace-pre-wrap break-all overflow-x-auto max-h-48">
          {lines.slice(0, COLLAPSE_THRESHOLD).join('\n')}
        </pre>
      </div>
    )
  }

  return (
    <div className="text-xs font-mono rounded bg-gray-900 text-gray-100 overflow-hidden">
      <pre className="p-3 whitespace-pre-wrap break-all overflow-x-auto max-h-96">
        {cleanOutput}
      </pre>
    </div>
  )
}

function ChevronIcon({ expanded }: { expanded: boolean }) {
  return (
    <svg
      className={`h-3 w-3 text-gray-400 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`}
      viewBox="0 0 20 20"
      fill="currentColor"
    >
      <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
    </svg>
  )
}

function FileOperationBadge({ operation }: { operation: 'created' | 'modified' | 'deleted' | 'moved' }) {
  const styles: Record<string, string> = {
    created: 'bg-green-100 text-green-700',
    modified: 'bg-blue-100 text-blue-700',
    deleted: 'bg-red-100 text-red-700',
    moved: 'bg-purple-100 text-purple-700',
  }
  const labels: Record<string, string> = {
    created: 'A',
    modified: 'M',
    deleted: 'D',
    moved: 'R',
  }
  return (
    <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium ${styles[operation]}`}>
      {labels[operation]}
    </span>
  )
}

function FileRow({ change }: { change: FileChangeSummary }) {
  return (
    <div className="flex items-center gap-2 py-1 px-2 hover:bg-gray-50 rounded">
      <FileOperationBadge operation={change.operation} />
      <span className="text-xs font-mono text-gray-700 truncate flex-1">{change.path}</span>
      {change.operation === 'moved' && change.oldPath && (
        <span className="text-xs text-gray-400">← {change.oldPath}</span>
      )}
      {change.additions !== undefined && change.additions > 0 && (
        <span className="text-xs text-green-600">+{change.additions}</span>
      )}
      {change.deletions !== undefined && change.deletions > 0 && (
        <span className="text-xs text-red-600">-{change.deletions}</span>
      )}
    </div>
  )
}

function PatchFilesView({ changes }: { changes: FileChangeSummary[] }) {
  const [expanded, setExpanded] = useState(false)

  return (
    <div className="rounded border border-gray-200 overflow-hidden">
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 rounded-none hover:bg-gray-50 transition-colors"
      >
        <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M4 4a2 2 0 012-2h4.586A2 2 0 0112 2.586L15.414 6A2 2 0 0116 7.414V16a2 2 0 01-2 2H6a2 2 0 01-2-2V4z" clipRule="evenodd" />
        </svg>
        <span className="text-xs font-medium text-gray-700">
          {changes.length} file{changes.length !== 1 ? 's' : ''} changed
        </span>
        {changes.length <= 3 && (
          <span className="text-xs text-gray-400">
            {changes.map(c => c.path.split('/').pop()).join(', ')}
          </span>
        )}
        <ChevronIcon expanded={expanded} />
      </Button>
      {expanded && (
        <div className="border-t border-gray-100 max-h-48 overflow-auto">
          {changes.map((change, i) => (
            <div key={i} className="border-b border-gray-50 last:border-b-0">
              <FileRow change={change} />
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function StatusIcon({ state }: { state: ToolCallEntry['state'] }) {
  if (state === 'started') {
    return (
      <svg
        data-testid="tool-call-card-status-icon"
        data-tone="info"
        className="h-3.5 w-3.5 text-info animate-spin"
        viewBox="0 0 24 24"
        fill="none"
      >
        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
      </svg>
    )
  }
  if (state === 'completed') {
    return (
      <svg
        data-testid="tool-call-card-status-icon"
        data-tone="success"
        className="h-3.5 w-3.5 text-success"
        viewBox="0 0 20 20"
        fill="currentColor"
      >
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
      </svg>
    )
  }
  return (
    <svg
      data-testid="tool-call-card-status-icon"
      data-tone="danger"
      className="h-3.5 w-3.5 text-danger"
      viewBox="0 0 20 20"
      fill="currentColor"
    >
      <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
    </svg>
  )
}

function EditToolCard({ entry }: { entry: ToolCallEntry }) {
  const parsed = parseEditInput(entry.rawInput)
  const isNewFile = entry.toolName === 'write' && parsed && !parsed.oldString

  const changedFiles: FileChangeSummary[] = entry.changedFiles ?? (parsed?.patch ? parsePatchOperations(parsed.patch) : [])
  const editWriteFiles: FileChangeSummary[] = (entry.toolName === 'write' || entry.toolName === 'edit') && parsed ? parseEditWriteChanges(parsed) : []

  const allChangedFiles = changedFiles.length > 0 ? changedFiles : editWriteFiles

  if (entry.state === 'started') {
    return (
      <div
        data-testid="edit-tool-card-running"
        data-tone="info"
        className="rounded-md border border-info-border bg-info-subtle/30 overflow-hidden"
      >
        <div className="flex items-center gap-2 px-3 py-1.5 border-b border-info-border">
          <StatusIcon state="started" />
          <span className="text-xs font-medium text-info">
            {isNewFile ? 'Creating' : 'Editing'} {parsed?.filePath ? parsed.filePath.split('/').pop() : '...'}
          </span>
          <span className="text-xs text-info">editing...</span>
        </div>
        {parsed?.filePath && (
          <div className="px-3 py-1 text-xs text-muted-foreground font-mono border-b border-border bg-muted/50">
            {parsed.filePath}
          </div>
        )}
      </div>
    )
  }

  const [showRaw, setShowRaw] = useState(false)

  const hasFileSummary = allChangedFiles.length > 0

  if (!parsed && !hasFileSummary) {
    return <GenericToolCard entry={entry} />
  }

  return (
    <div
      data-tone={entry.state === 'failed' ? 'danger' : 'neutral'}
      className={`rounded-md border overflow-hidden ${entry.state === 'failed' ? 'border-danger-border' : 'border-border'}`}
    >
      <div className="flex items-center gap-2 px-3 py-1.5 border-b border-border bg-muted/50">
        <StatusIcon state={entry.state} />
        <span className="text-xs font-medium text-foreground">
          {isNewFile ? 'Created' : 'Edited'}
        </span>
        {hasFileSummary ? (
          <span className="text-xs text-muted-foreground">
            {allChangedFiles.length} file{allChangedFiles.length !== 1 ? 's' : ''}
          </span>
        ) : (
          <span className="text-xs text-muted-foreground font-mono truncate">{parsed?.filePath.split('/').pop() ?? parsed?.filePath ?? ''}</span>
        )}
        {entry.duration != null && (
          <span className="text-xs text-muted-foreground/70 ml-auto">{formatDuration(entry.duration)}</span>
        )}
      </div>

      {hasFileSummary ? (
        <PatchFilesView changes={allChangedFiles} />
      ) : null}

      {showRaw && parsed?.patch && !parsed.oldString && !parsed.newString && (
        <div className="border-t border-gray-100">
          <PatchBlock patch={parsed.patch} />
        </div>
      )}
      {showRaw && (!parsed || (parsed && (parsed.oldString || parsed.newString))) && (
        <div className="border-t border-gray-100 px-3 py-2 space-y-2">
          {parsed && parsed.oldString && parsed.newString && (
            <div>
              <div className="text-xs font-medium text-gray-500 mb-1">Changes</div>
              <DiffBlock oldStr={parsed.oldString} newStr={parsed.newString} />
            </div>
          )}
          {entry.rawInput && (
            <div>
              <div className="text-xs font-medium text-gray-500 mb-1">Input</div>
              <pre className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
                {entry.rawInput}
              </pre>
            </div>
          )}
          {entry.rawOutput && (
            <div>
              <div className="text-xs font-medium text-gray-500 mb-1">Output</div>
              <pre className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
                {entry.rawOutput}
              </pre>
            </div>
          )}
        </div>
      )}

      {hasFileSummary && (parsed?.patch || entry.rawInput || entry.rawOutput || parsed?.oldString || parsed?.newString) && (
        <Button
          variant="link"
          onClick={() => setShowRaw(!showRaw)}
          className="h-auto w-full rounded-none text-xs text-info hover:text-info py-1 text-center border-t border-border hover:bg-muted transition-colors"
        >
          {showRaw ? 'Hide raw' : 'Show raw patch'}
        </Button>
      )}

      {entry.state === 'failed' && entry.error && (
        <div
          data-testid="edit-tool-card-error"
          data-tone="danger"
          className="px-3 py-1.5 text-xs text-danger bg-danger-subtle border-t border-danger-border"
        >
          {entry.error}
        </div>
      )}
    </div>
  )
}

function BashToolCard({ entry }: { entry: ToolCallEntry }) {
  const parsed = parseBashInput(entry.rawInput)
  const output = entry.rawOutput ?? entry.result ?? ''
  const outputLines = output ? output.split('\n') : []
  const COLLAPSE_THRESHOLD = 10
  const [expanded, setExpanded] = useState(false)
  const shouldCollapse = outputLines.length > COLLAPSE_THRESHOLD
  const isFailed = entry.state === 'failed'

  if (entry.state === 'started') {
    return (
      <div
        data-testid="bash-tool-card-running"
        data-tone="info"
        className="rounded-md border border-info-border bg-info-subtle/30 overflow-hidden"
      >
        <div className="flex items-center gap-2 px-3 py-1.5">
          <StatusIcon state="started" />
          <span className="text-xs text-muted-foreground/70 select-none mr-1">$</span>
          <span className="text-xs font-mono text-foreground truncate">{parsed?.command ?? '...'}</span>
          <span className="text-xs text-info">running...</span>
        </div>
      </div>
    )
  }

  return (
    <div
      data-tone={isFailed ? 'danger' : 'neutral'}
      className={`rounded-md border overflow-hidden ${isFailed ? 'border-danger-border' : 'border-border'}`}
    >
      <div className={`flex items-center gap-2 px-3 py-1.5 ${isFailed ? 'bg-danger-subtle/50' : 'bg-muted'}`}>
        <StatusIcon state={entry.state} />
        <span className="text-xs text-muted-foreground/70 select-none mr-1">$</span>
        <span className="text-xs font-mono text-foreground truncate flex-1">{parsed?.command ?? 'bash'}</span>
        {entry.duration != null && (
          <span className="text-xs text-muted-foreground/70">{formatDuration(entry.duration)}</span>
        )}
      </div>
      {output && (
        <div className={isFailed ? 'border-l-2 border-danger-border' : ''}>
          <TerminalBlock output={output} collapsed={expanded} />
          {shouldCollapse && !expanded && (
            <Button
              variant="link"
              onClick={() => setExpanded(true)}
              className="h-auto w-full rounded-none text-xs text-info hover:text-info py-1 text-center bg-muted-foreground hover:bg-muted-foreground/80 transition-colors"
            >
              Show more ({outputLines.length - COLLAPSE_THRESHOLD} more lines)
            </Button>
          )}
          {expanded && shouldCollapse && (
            <Button
              variant="link"
              onClick={() => setExpanded(false)}
              className="h-auto w-full rounded-none text-xs text-info hover:text-info py-1 text-center bg-muted-foreground hover:bg-muted-foreground/80 transition-colors"
            >
              Show less
            </Button>
          )}
        </div>
      )}
      {isFailed && entry.error && !output && (
        <div
          data-testid="bash-tool-card-error"
          data-tone="danger"
          className="px-3 py-1.5 text-xs text-danger bg-danger-subtle border-l-2 border-danger-border"
        >
          {entry.error}
        </div>
      )}
    </div>
  )
}

function SummaryToolCard({ entry }: { entry: ToolCallEntry }) {
  const [expanded, setExpanded] = useState(false)
  const displayInput = entry.rawInput ?? entry.args
  const displayOutput = entry.rawOutput ?? entry.result

  const label = getToolLabel(entry.toolName, displayInput)
  const args = getToolArgs(entry.toolName, displayInput)

  let summary = entry.toolName
  if (label) {
    summary = label
  } else if (args.length > 0) {
    summary = `${entry.toolName} (${args.join(', ')})`
  }

  if (entry.state === 'started') {
    return (
      <div
        data-testid="summary-tool-card-running"
        data-tone="info"
        className="flex items-center gap-2 px-2 py-1 text-xs"
      >
        <StatusIcon state="started" />
        <span className="font-mono text-muted-foreground">{entry.toolName}</span>
        {label ? (
          <span className="text-info truncate max-w-[200px]">{label}</span>
        ) : (
          <span className="text-info">running...</span>
        )}
      </div>
    )
  }

  return (
    <div>
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-2 py-1 hover:bg-gray-50 rounded transition-colors"
      >
        <StatusIcon state={entry.state} />
        <span className="font-mono text-xs text-gray-600">{summary}</span>
        {args.length > 0 && !label && (
          <span className="flex gap-1 shrink-0">
            {args.slice(0, 2).map((arg, i) => (
              <span key={i} className="inline-flex items-center px-1 py-0.5 rounded bg-gray-100 text-xs text-gray-500 font-mono">
                {arg}
              </span>
            ))}
          </span>
        )}
        {entry.duration != null && (
          <span className="text-xs text-gray-400">{formatDuration(entry.duration)}</span>
        )}
        <ChevronIcon expanded={expanded} />
      </Button>
      {expanded && (
        <div className="ml-6 mt-1 space-y-1.5 text-xs">
          {displayInput && (
            <div>
              <div className="font-medium text-gray-500 mb-0.5">Input</div>
              <pre className="whitespace-pre-wrap break-all text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
                {tryFormatJson(typeof displayInput === 'string' ? displayInput : JSON.stringify(displayInput))}
              </pre>
            </div>
          )}
          {displayOutput && (
            <div>
              <div className="font-medium text-gray-500 mb-0.5">Output</div>
              <pre className="whitespace-pre-wrap break-all text-gray-700 bg-gray-50 rounded p-2 max-h-48 overflow-auto">
                {tryFormatJson(typeof displayOutput === 'string' ? displayOutput : JSON.stringify(displayOutput))}
              </pre>
            </div>
          )}
          {entry.state === 'failed' && entry.error && (
            <div
              data-testid="summary-tool-card-error"
              data-tone="danger"
              className="text-danger"
            >
              {entry.error}
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function ToolIcon({ toolName, className }: { toolName: string; className?: string }) {
  const iconMap: Record<string, JSX.Element> = {
    webfetch: (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="12" r="10" />
        <path d="M2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" />
      </svg>
    ),
    task: (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <rect x="3" y="3" width="18" height="18" rx="2" />
        <path d="M9 12h6M12 9v6" />
      </svg>
    ),
    skill: (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2" />
      </svg>
    ),
    search: (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="11" cy="11" r="8" />
        <path d="m21 21-4.35-4.35" />
      </svg>
    ),
    read: (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
        <polyline points="14 2 14 8 20 8" />
        <line x1="16" y1="13" x2="8" y2="13" />
        <line x1="16" y1="17" x2="8" y2="17" />
        <polyline points="10 9 9 9 8 9" />
      </svg>
    ),
    write: (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M12 20h9" />
        <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z" />
      </svg>
    ),
    edit: (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M12 20h9" />
        <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z" />
      </svg>
    ),
    bash: (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <polyline points="4 17 10 11 4 5" />
        <line x1="12" y1="19" x2="20" y2="19" />
      </svg>
    ),
    glob: (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
        <path d="M2 10h20" />
      </svg>
    ),
    grep: (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="11" cy="11" r="8" />
        <path d="m21 21-4.35-4.35" />
        <path d="M8 8h6" />
      </svg>
    ),
  }

  const defaultIcon = (
    <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <path d="M12 8v8M8 12h8" />
    </svg>
  )

  return iconMap[toolName] ?? defaultIcon
}

function GenericToolCard({ entry }: { entry: ToolCallEntry }) {
  const [expanded, setExpanded] = useState(false)
  const displayInput = entry.rawInput ?? entry.args
  const displayOutput = entry.rawOutput ?? entry.result
  const label = getToolLabel(entry.toolName, displayInput)
  const args = getToolArgs(entry.toolName, displayInput)

  if (entry.state === 'started') {
    return (
      <div
        data-testid="generic-tool-card-running"
        data-tone="info"
        className="flex items-center gap-2 px-2 py-1 text-xs"
      >
        <StatusIcon state="started" />
        <ToolIcon toolName={entry.toolName} className="h-3.5 w-3.5 text-muted-foreground/70" />
        <span className="font-mono text-muted-foreground">Called {entry.toolName}</span>
        {label ? (
          <span className="text-info truncate max-w-[200px]">{label}</span>
        ) : (
          <span className="text-info">running...</span>
        )}
      </div>
    )
  }

  const isFailed = entry.state === 'failed'

  return (
    <div
      data-tone={isFailed ? 'danger' : 'neutral'}
      className={`rounded-md border overflow-hidden ${isFailed ? 'border-danger-border' : 'border-border'}`}
    >
      <Button
        variant="ghost"
        size="sm"
        onClick={() => setExpanded(!expanded)}
        className="flex h-auto items-center justify-start gap-2 w-full text-left px-3 py-1.5 rounded-none hover:bg-muted transition-colors"
      >
        <StatusIcon state={entry.state} />
        <ToolIcon toolName={entry.toolName} className="h-3.5 w-3.5 text-muted-foreground/70 shrink-0" />
        <span className="text-xs font-medium text-foreground">Called {entry.toolName}</span>
        {label && (
          <span className="text-xs text-muted-foreground truncate max-w-[200px]">{label}</span>
        )}
        {args.length > 0 && (
          <span className="flex gap-1 shrink-0">
            {args.slice(0, 3).map((arg, i) => (
              <span key={i} className="inline-flex items-center px-1.5 py-0.5 rounded bg-muted text-xs text-muted-foreground font-mono">
                {arg}
              </span>
            ))}
          </span>
        )}
        {entry.duration != null && (
          <span className="text-xs text-muted-foreground/70 ml-auto shrink-0">{formatDuration(entry.duration)}</span>
        )}
        <ChevronIcon expanded={expanded} />
      </Button>

      {isFailed && entry.error && (
        <div
          data-testid="generic-tool-card-error"
          data-tone="danger"
          className="px-3 py-1.5 text-xs text-danger bg-danger-subtle border-t border-danger-border"
        >
          {entry.error}
        </div>
      )}

      {expanded && (
        <div className="px-3 pb-2 space-y-1.5 text-xs border-t border-gray-100">
          {displayInput && (
            <div className="pt-1.5">
              <div className="font-medium text-gray-500 mb-0.5">Input</div>
              <pre className="whitespace-pre-wrap break-all text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
                {tryFormatJson(typeof displayInput === 'string' ? displayInput : JSON.stringify(displayInput))}
              </pre>
            </div>
          )}
          {displayOutput && (
            <div>
              <div className="font-medium text-gray-500 mb-0.5">Output</div>
              <pre className="whitespace-pre-wrap break-all text-gray-700 bg-gray-50 rounded p-2 max-h-48 overflow-auto">
                {tryFormatJson(typeof displayOutput === 'string' ? displayOutput : JSON.stringify(displayOutput))}
              </pre>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

export function ToolCallCard({ entry, compact = false }: { entry: ToolCallEntry; compact?: boolean }) {
  const displayType = getDisplayType(entry.toolName)

  if (compact) {
    return (
      <div
        data-testid="tool-call-card-compact"
        data-tone={entry.state === 'failed' ? 'danger' : 'neutral'}
        className={`flex items-center gap-2 px-2 py-1 text-xs rounded border border-border bg-muted/50 ${entry.state === 'failed' ? 'border-danger-border' : ''}`}
      >
        <StatusIcon state={entry.state} />
        <span className="font-mono text-muted-foreground">{entry.toolName}</span>
        {entry.duration != null && (
          <span className="text-xs text-muted-foreground/70">{formatDuration(entry.duration)}</span>
        )}
      </div>
    )
  }

  switch (displayType) {
    case 'diff':
      return <EditToolCard entry={entry} />
    case 'terminal':
      return <BashToolCard entry={entry} />
    case 'summary':
      return <SummaryToolCard entry={entry} />
    case 'generic':
    default:
      return <GenericToolCard entry={entry} />
  }
}
