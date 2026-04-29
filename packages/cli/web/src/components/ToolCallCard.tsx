import { useState } from 'react'
import type { ToolCallEntry } from '../lib/types'

export interface EditInput {
  filePath: string
  oldString: string
  newString: string
}

export interface BashInput {
  command: string
}

export type ToolDisplayType = 'diff' | 'terminal' | 'summary' | 'generic'

export const TOOL_DISPLAY_TYPE: Record<string, ToolDisplayType> = {
  edit: 'diff',
  write: 'diff',
  bash: 'terminal',
  read: 'summary',
  glob: 'summary',
  grep: 'summary',
  todowrite: 'summary',
  webfetch: 'summary',
  memread: 'summary',
  membrowse: 'summary',
  memsearch: 'summary',
}

export function parseEditInput(rawInput: string | undefined): EditInput | null {
  if (!rawInput) return null
  try {
    const parsed = JSON.parse(rawInput)
    if (typeof parsed !== 'object' || parsed === null) return null
    const filePath = parsed.file_path ?? parsed.filePath ?? parsed.path ?? ''
    const oldString = parsed.oldString ?? ''
    const newString = parsed.newString ?? parsed.content ?? ''
    return { filePath, oldString, newString }
  } catch {
    return null
  }
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

function tryFormatJson(text: string): string {
  try {
    const parsed = JSON.parse(text)
    return JSON.stringify(parsed, null, 2)
  } catch {
    return text
  }
}

function getDisplayType(toolName: string): ToolDisplayType {
  return TOOL_DISPLAY_TYPE[toolName] ?? 'generic'
}

function DiffBlock({ oldStr, newStr }: { oldStr: string; newStr: string }) {
  const oldLines = oldStr ? oldStr.split('\n') : []
  const newLines = newStr ? newStr.split('\n') : []

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
  const lines = output.split('\n')
  const COLLAPSE_THRESHOLD = 10
  const shouldCollapse = !collapsed && lines.length > COLLAPSE_THRESHOLD

  return (
    <div className="text-xs font-mono rounded bg-gray-900 text-gray-100 overflow-hidden">
      <pre className="p-3 whitespace-pre-wrap break-all overflow-x-auto max-h-96">
        {shouldCollapse ? lines.slice(0, COLLAPSE_THRESHOLD).join('\n') : output}
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

function StatusIcon({ state }: { state: ToolCallEntry['state'] }) {
  if (state === 'started') {
    return (
      <svg className="h-3.5 w-3.5 text-blue-500 animate-spin" viewBox="0 0 24 24" fill="none">
        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
      </svg>
    )
  }
  if (state === 'completed') {
    return (
      <svg className="h-3.5 w-3.5 text-green-500" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
      </svg>
    )
  }
  return (
    <svg className="h-3.5 w-3.5 text-red-500" viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
    </svg>
  )
}

function EditToolCard({ entry }: { entry: ToolCallEntry }) {
  const parsed = parseEditInput(entry.rawInput)
  const isNewFile = entry.toolName === 'write' && parsed && !parsed.oldString

  if (entry.state === 'started') {
    return (
      <div className="rounded-md border border-blue-200 bg-blue-50/30 overflow-hidden">
        <div className="flex items-center gap-2 px-3 py-1.5 border-b border-blue-100">
          <StatusIcon state="started" />
          <span className="text-xs font-medium text-blue-700">
            {isNewFile ? 'Creating' : 'Editing'} {parsed?.filePath ? parsed.filePath.split('/').pop() : '...'}
          </span>
          <span className="text-xs text-blue-500">editing...</span>
        </div>
        {parsed?.filePath && (
          <div className="px-3 py-1 text-xs text-gray-500 font-mono border-b border-gray-100 bg-gray-50/50">
            {parsed.filePath}
          </div>
        )}
      </div>
    )
  }

  if (!parsed) {
    return <GenericToolCard entry={entry} />
  }

  const fileName = parsed.filePath.split('/').pop() ?? parsed.filePath

  return (
    <div className={`rounded-md border overflow-hidden ${entry.state === 'failed' ? 'border-red-200' : 'border-gray-200'}`}>
      <div className="flex items-center gap-2 px-3 py-1.5 border-b border-gray-100 bg-gray-50/50">
        <StatusIcon state={entry.state} />
        <span className="text-xs font-medium text-gray-700">
          {isNewFile ? 'Created' : 'Edited'}
        </span>
        <span className="text-xs text-gray-500 font-mono truncate">{fileName}</span>
        {entry.duration != null && (
          <span className="text-xs text-gray-400 ml-auto">{formatDuration(entry.duration)}</span>
        )}
      </div>
      <div className="px-3 py-1 text-xs text-gray-400 font-mono border-b border-gray-100 bg-gray-50/30">
        {parsed.filePath}
      </div>
      <DiffBlock oldStr={parsed.oldString} newStr={parsed.newString} />
      {entry.state === 'failed' && entry.error && (
        <div className="px-3 py-1.5 text-xs text-red-600 bg-red-50 border-t border-red-100">
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
      <div className="rounded-md border border-blue-200 bg-blue-50/30 overflow-hidden">
        <div className="flex items-center gap-2 px-3 py-1.5">
          <StatusIcon state="started" />
          <span className="text-xs text-gray-400 select-none mr-1">$</span>
          <span className="text-xs font-mono text-gray-700 truncate">{parsed?.command ?? '...'}</span>
          <span className="text-xs text-blue-500">running...</span>
        </div>
      </div>
    )
  }

  return (
    <div className={`rounded-md border overflow-hidden ${isFailed ? 'border-red-200' : 'border-gray-200'}`}>
      <div className={`flex items-center gap-2 px-3 py-1.5 ${isFailed ? 'bg-red-50/50' : 'bg-gray-100'}`}>
        <StatusIcon state={entry.state} />
        <span className="text-xs text-gray-400 select-none mr-1">$</span>
        <span className="text-xs font-mono text-gray-700 truncate flex-1">{parsed?.command ?? 'bash'}</span>
        {entry.duration != null && (
          <span className="text-xs text-gray-400">{formatDuration(entry.duration)}</span>
        )}
      </div>
      {output && (
        <div className={isFailed ? 'border-l-2 border-red-400' : ''}>
          <TerminalBlock output={output} collapsed={expanded} />
          {shouldCollapse && !expanded && (
            <button
              onClick={() => setExpanded(true)}
              className="w-full text-xs text-blue-500 hover:text-blue-700 py-1 text-center bg-gray-800 hover:bg-gray-700 transition-colors"
            >
              Show more ({outputLines.length - COLLAPSE_THRESHOLD} more lines)
            </button>
          )}
          {expanded && shouldCollapse && (
            <button
              onClick={() => setExpanded(false)}
              className="w-full text-xs text-blue-500 hover:text-blue-700 py-1 text-center bg-gray-800 hover:bg-gray-700 transition-colors"
            >
              Show less
            </button>
          )}
        </div>
      )}
      {isFailed && entry.error && !output && (
        <div className="px-3 py-1.5 text-xs text-red-600 bg-red-50 border-l-2 border-red-400">
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

  let summary = entry.toolName
  if (displayInput) {
    try {
      const parsed = JSON.parse(typeof displayInput === 'string' ? displayInput : JSON.stringify(displayInput))
      if (parsed.filePath || parsed.file_path || parsed.path) {
        summary = `${entry.toolName} ${(parsed.filePath ?? parsed.file_path ?? parsed.path).split('/').pop()}`
      } else if (parsed.pattern || parsed.query) {
        summary = `${entry.toolName} ${parsed.pattern ?? parsed.query}`
      } else if (parsed.todos) {
        summary = `${entry.toolName} (${parsed.todos.length} items)`
      }
    } catch {
      summary = entry.toolName
    }
  }

  if (entry.state === 'started') {
    return (
      <div className="flex items-center gap-2 px-2 py-1 text-xs">
        <StatusIcon state="started" />
        <span className="font-mono text-gray-600">{summary}</span>
        <span className="text-blue-500">running...</span>
      </div>
    )
  }

  return (
    <div>
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center gap-2 w-full text-left px-2 py-1 hover:bg-gray-50 rounded transition-colors"
      >
        <StatusIcon state={entry.state} />
        <span className="font-mono text-xs text-gray-600">{summary}</span>
        {entry.duration != null && (
          <span className="text-xs text-gray-400">{formatDuration(entry.duration)}</span>
        )}
        <ChevronIcon expanded={expanded} />
      </button>
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
            <div className="text-red-600">{entry.error}</div>
          )}
        </div>
      )}
    </div>
  )
}

function GenericToolCard({ entry }: { entry: ToolCallEntry }) {
  const [expanded, setExpanded] = useState(false)
  const displayInput = entry.rawInput ?? entry.args
  const displayOutput = entry.rawOutput ?? entry.result

  if (entry.state === 'started') {
    return (
      <div className="flex items-center gap-2 px-2 py-1 text-xs">
        <StatusIcon state="started" />
        <span className="font-mono text-gray-600">{entry.toolName}</span>
        <span className="text-blue-500">running...</span>
      </div>
    )
  }

  return (
    <div className="rounded-md border border-gray-200 overflow-hidden">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center gap-2 w-full text-left px-3 py-1.5 hover:bg-gray-50 transition-colors"
      >
        <StatusIcon state={entry.state} />
        <span className="font-mono text-xs text-gray-600">{entry.toolName}</span>
        {entry.duration != null && (
          <span className="text-xs text-gray-400">{formatDuration(entry.duration)}</span>
        )}
        {entry.state === 'failed' && entry.error && (
          <span className="text-xs text-red-500 truncate">{entry.error}</span>
        )}
        <ChevronIcon expanded={expanded} />
      </button>
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

export function ToolCallCard({ entry }: { entry: ToolCallEntry }) {
  const displayType = getDisplayType(entry.toolName)

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
