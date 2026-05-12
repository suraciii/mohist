import React, { useState } from 'react'
import Markdown from 'react-markdown'
import type { DisplayAssistantPart, DisplayChangedFile } from '../../lib/session-transcript-display'

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString()
}

interface AssistantTextPartViewProps {
  text: string
}

export function AssistantTextPartView({ text }: AssistantTextPartViewProps) {
  return (
    <div className="max-w-[90%]">
      <div className="text-sm text-gray-800 leading-relaxed">
        <Markdown
          components={{
            code({ children, className }) {
              const match = /language-(\w+)/.exec(className ?? '')
              const isInline = !match && !className
              if (isInline) {
                return <code className="px-1 py-0.5 bg-gray-100 rounded text-gray-800 text-xs font-mono">{children}</code>
              }
              return (
                <code className={`${className ?? ''} block overflow-x-auto rounded bg-gray-50 p-3 text-xs font-mono`}>
                  {children}
                </code>
              )
            },
            pre({ children }) {
              return <pre className="overflow-x-auto rounded bg-gray-50 p-3 text-xs font-mono">{children}</pre>
            },
          }}
        >
          {text}
        </Markdown>
      </div>
    </div>
  )
}

interface ReasoningPartViewProps {
  text: string
  startedAt: string
}

export function ReasoningPartView({ text, startedAt }: ReasoningPartViewProps) {
  const sizeKB = (text.length / 1024).toFixed(1)

  return (
    <details className="max-w-[90%]">
      <summary className="text-xs text-gray-400 cursor-pointer hover:text-gray-600 select-none">
        Thinking... {sizeKB}KB · {formatTime(startedAt)}
      </summary>
      <pre data-scrollable="" className="mt-1 text-xs text-gray-500 whitespace-pre-wrap break-all max-h-48 overflow-auto bg-gray-50 rounded p-2">
        {text}
      </pre>
    </details>
  )
}

interface ErrorPartViewProps {
  message: string
  kind: 'timeout' | 'failed' | 'cancelled' | 'recovery'
  at: string
}

export function ErrorPartView({ message, kind, at }: ErrorPartViewProps) {
  const messages: Record<string, string> = {
    timeout: '⏱️ Execution timed out',
    failed: '✗ Execution failed',
    cancelled: '⊘ Execution cancelled',
    recovery: '↻ Recovery in progress',
  }

  return (
    <div className="flex items-center gap-1.5 text-xs text-amber-600">
      <svg className="h-3 w-3 shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 5a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 5zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
      </svg>
      <span>
        {messages[kind] ?? kind}
        {message && message !== (messages[kind] ?? kind) ? `: ${message}` : ''}
        {' · '}
        {formatTime(at)}
      </span>
    </div>
  )
}

interface DividerPartViewProps {
  label: string
}

export function DividerPartView({ label }: DividerPartViewProps) {
  return (
    <div className="flex items-center gap-2 py-2">
      <div className="flex-1 border-t border-gray-200" />
      <span className="text-xs text-gray-400">{label}</span>
      <div className="flex-1 border-t border-gray-200" />
    </div>
  )
}

interface AssistantPartsProps {
  parts: DisplayAssistantPart[]
}

export function AssistantParts({ parts }: AssistantPartsProps) {
  return (
    <div className="space-y-2">
      {parts.map((part) => {
        switch (part.partType) {
          case 'text':
            return <AssistantTextPartView key={part.id} text={part.text} />
          case 'reasoning':
            return <ReasoningPartView key={part.id} text={part.text} startedAt={part.startedAt} />
          case 'tool':
            return <ToolRowView key={part.id} part={part} />
          case 'context-group':
            return <ContextGroupView key={part.id} title={part.title} tools={part.tools} hasError={part.hasError} />
          case 'error':
            return <ErrorPartView key={part.id} message={part.message} kind={part.kind} at={part.at} />
          case 'divider':
            return <DividerPartView key={part.id} label={part.label} />
          default:
            return null
        }
      })}
    </div>
  )
}

const TOOL_ICONS: Record<string, React.ReactElement> = {
  read: (
    <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
      <polyline points="14 2 14 8 20 8" />
      <line x1="16" y1="13" x2="8" y2="13" />
      <line x1="16" y1="17" x2="8" y2="17" />
      <polyline points="10 9 9 9 8 9" />
    </svg>
  ),
  glob: (
    <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
      <path d="M2 10h20" />
    </svg>
  ),
  grep: (
    <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="11" cy="11" r="8" />
      <path d="m21 21-4.35-4.35" />
      <path d="M8 8h6" />
    </svg>
  ),
  search: (
    <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="11" cy="11" r="8" />
      <path d="m21 21-4.35-4.35" />
    </svg>
  ),
  bash: (
    <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <polyline points="4 17 10 11 4 5" />
      <line x1="12" y1="19" x2="20" y2="19" />
    </svg>
  ),
  apply_patch: (
    <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M4 4a2 2 0 012-2h4.586A2 2 0 0112 2.586L15.414 6A2 2 0 0116 7.414V16a2 2 0 01-2 2H6a2 2 0 01-2-2V4z" />
    </svg>
  ),
  edit: (
    <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 20h9" />
      <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z" />
    </svg>
  ),
  write: (
    <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 20h9" />
      <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z" />
    </svg>
  ),
  webfetch: (
    <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="12" r="10" />
      <path d="M2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" />
    </svg>
  ),
}

function ToolIcon({ normalizedName }: { normalizedName: string }) {
  const icon = TOOL_ICONS[normalizedName]
  if (icon) return icon
  return (
    <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <path d="M12 8v8M8 12h8" />
    </svg>
  )
}

function getToolDisplayLabel(normalizedName: string, displayTitle?: string, displaySubtitle?: string): string {
  if (displayTitle) return displayTitle
  if (displaySubtitle) return displaySubtitle
  return normalizedName
}

function parseJsonSafely(input: string | undefined): Record<string, unknown> | null {
  if (!input) return null
  try {
    const parsed = JSON.parse(input)
    if (typeof parsed !== 'object' || parsed === null) return null
    return parsed as Record<string, unknown>
  } catch {
    return null
  }
}

function getFilePathFromInput(input: string | undefined): string | null {
  const parsed = parseJsonSafely(input)
  if (!parsed) return null
  const fp = parsed.filePath ?? parsed.file_path ?? parsed.path
  if (typeof fp === 'string') return fp
  return null
}

function ToolStatusDot({ status }: { status: string }) {
  switch (status) {
    case 'running':
      return <span className="h-2 w-2 rounded-full bg-blue-400 animate-pulse shrink-0" />
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

interface PatchDiffViewProps {
  changedFiles: DisplayChangedFile[]
}

function PatchDiffView({ changedFiles }: PatchDiffViewProps) {
  const [expanded, setExpanded] = useState(false)

  if (changedFiles.length === 0) return null

  return (
    <div className="border-t border-gray-100">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center gap-2 w-full text-left px-3 py-1.5 text-xs text-blue-600 hover:text-blue-800 hover:bg-gray-50 transition-colors"
      >
        <svg className={`h-3 w-3 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
        {expanded ? 'Hide' : 'Show'} diff
      </button>
      {expanded && (
        <div className="px-3 pb-2 space-y-1">
          {changedFiles.map((change, i) => {
            const opBadge: Record<string, string> = { created: '+', modified: '~', deleted: '-', moved: '>' }
            return (
              <div key={i} className="flex items-center gap-2 py-0.5">
                <span className="text-xs font-mono text-gray-500 w-3">{opBadge[change.operation] ?? '?'}</span>
                <span className="text-xs font-mono text-gray-700 truncate flex-1">{change.path}</span>
                {change.additions !== undefined && change.additions > 0 && (
                  <span className="text-xs text-green-600">+{change.additions}</span>
                )}
                {change.deletions !== undefined && change.deletions > 0 && (
                  <span className="text-xs text-red-600">-{change.deletions}</span>
                )}
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}

interface ToolRowViewProps {
  part: Extract<DisplayAssistantPart, { partType: 'tool' }>
}

function ToolRowView({ part }: ToolRowViewProps) {
  const [expanded, setExpanded] = useState(false)
  const isRunning = part.status === 'running' || part.status === 'pending'
  const toolLabel = getToolDisplayLabel(part.normalizedName, part.displayTitle, part.displaySubtitle)
  const filePath = getFilePathFromInput(part.input)
  const hasChangedFiles = part.changedFiles && part.changedFiles.length > 0

  const showExpandableDetails = !isRunning && (part.input || part.output || part.error || hasChangedFiles)

  return (
    <div className={`rounded-md border overflow-hidden ${part.hasError ? 'border-red-200' : 'border-gray-200'}`}>
      <button
        onClick={showExpandableDetails ? () => setExpanded(!expanded) : undefined}
        className={`flex items-center gap-2 w-full text-left px-3 py-1.5 transition-colors ${showExpandableDetails ? 'hover:bg-gray-50 cursor-pointer' : 'cursor-default'}`}
      >
        <ToolStatusDot status={part.status} />
        <ToolIcon normalizedName={part.normalizedName} />
        <span className="text-xs font-medium text-gray-700">{toolLabel}</span>
        {filePath && part.normalizedName !== filePath && (
          <span className="text-xs text-gray-400 truncate max-w-[150px]">{filePath}</span>
        )}
        {part.hasError && (
          <span className="text-xs text-red-500">failed</span>
        )}
        {hasChangedFiles && (
          <span className="text-xs text-green-600">
            {part.changedFiles!.length === 1
              ? part.changedFiles![0].path.split('/').pop()
              : `${part.changedFiles!.length} files`}
          </span>
        )}
        {showExpandableDetails && (
          <svg className={`h-3 w-3 text-gray-400 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
          </svg>
        )}
      </button>
      {expanded && showExpandableDetails && (
        <div className="border-t border-gray-100 space-y-2">
          {part.input && (
            <div className="px-3 pt-2">
              <div className="font-medium text-xs text-gray-500 mb-1">Input</div>
              <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
                {part.input}
              </pre>
            </div>
          )}
          {part.output && (
            <div className="px-3">
              <div className="font-medium text-xs text-gray-500 mb-1">Output</div>
              <pre data-scrollable="" className="whitespace-pre-wrap break-all text-xs text-gray-700 bg-gray-50 rounded p-2 max-h-32 overflow-auto">
                {part.output}
              </pre>
            </div>
          )}
          {part.error && (
            <div className="px-3 text-xs text-red-600 bg-red-50">
              {part.error}
            </div>
          )}
          {hasChangedFiles && (
            <PatchDiffView changedFiles={part.changedFiles!} />
          )}
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

function ContextGroupView({ title, tools, hasError }: ContextGroupViewProps) {
  const [expanded, setExpanded] = useState(false)

  return (
    <div className="rounded-md border border-gray-200 overflow-hidden">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center gap-2 w-full text-left px-3 py-1.5 hover:bg-gray-50 transition-colors"
      >
        <svg className="h-3.5 w-3.5 text-gray-400 shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path d="M10 3a1.5 1.5 0 110 3 1.5 1.5 0 010-3zM7.5 4.5a1.5 1.5 0 110 3 1.5 1.5 0 010-3zm5 0a1.5 1.5 0 110 3 1.5 1.5 0 010-3z" />
        </svg>
        <span className="text-xs font-medium text-gray-700">{title}</span>
        {hasError && (
          <span className="text-xs text-red-500">failed</span>
        )}
        <svg className={`h-3 w-3 text-gray-400 shrink-0 ml-auto transition-transform ${expanded ? 'rotate-90' : ''}`} viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
        </svg>
      </button>
      {expanded && (
        <div className="px-3 pb-2 border-t border-gray-100 space-y-1.5">
          {tools.map((tool) => (
            <ToolRowView key={tool.id} part={tool} />
          ))}
        </div>
      )}
    </div>
  )
}
